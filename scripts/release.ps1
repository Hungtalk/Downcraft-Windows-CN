#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the DownCraft release packages (portable, self-contained and lite).

.DESCRIPTION
    Produces, under artifacts\release:

      DownCraft-<version>-win-x64.zip          self-contained, runs without .NET installed
      DownCraft-<version>-win-x64-lite.zip     framework-dependent, needs .NET 10 Desktop Runtime
      SHA256SUMS.txt                           checksums for both archives

    Each archive contains downcraft.exe, DownCraft.Gui.exe, downcraft_native.dll,
    README.md, LICENSE and CHANGELOG.md under a DownCraft\ folder.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 -SkipNative -NoSelfContained
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipNative,
    [switch]$NoSelfContained,
    [switch]$SkipLite
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $root 'artifacts\release'

if (-not $Version) {
    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    if ($props -match '<Version>([^<]+)</Version>') {
        $Version = $Matches[1].Trim()
    }
    else {
        throw 'Could not read <Version> from Directory.Build.props; pass -Version.'
    }
}

Push-Location $root
try {
    Write-Host ("== DownCraft release {0} ({1}) ==" -f $Version, $Runtime) -ForegroundColor Cyan

    if (-not $SkipNative) {
        & (Join-Path $PSScriptRoot 'build-native.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'native build failed' }
    }

    Remove-Item $releaseRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $releaseRoot | Out-Null

    function New-Package([string]$Name, [bool]$SelfContained) {
        $staging = Join-Path $releaseRoot "staging-$Name"
        $app = Join-Path $staging 'DownCraft'
        New-Item -ItemType Directory -Force $app | Out-Null

        # Folder publish (no single-file bundle): bundling extracts to %TEMP%\.net on every run,
        # which fails on locked-down machines where TEMP is not writable for portable apps.
        $common = @(
            '-c', 'Release',
            '-r', $Runtime,
            '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
            '-p:PublishSingleFile=false',
            '-p:DebugType=none',
            '--nologo'
        )

        Write-Host ("-- publish {0} (self-contained={1})" -f $Name, $SelfContained) -ForegroundColor Cyan
        # Out-Host keeps dotnet's stdout out of this function's return value.
        dotnet publish (Join-Path $root 'src\DownCraft.Cli\DownCraft.Cli.csproj') @common -o $app 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "CLI publish failed ($LASTEXITCODE)" }

        dotnet publish (Join-Path $root 'src\DownCraft.Gui\DownCraft.Gui.csproj') @common -o $app 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "GUI publish failed ($LASTEXITCODE)" }

        Copy-Item (Join-Path $root 'README.md') $app -Force
        Copy-Item (Join-Path $root 'LICENSE') $app -Force
        Copy-Item (Join-Path $root 'CHANGELOG.md') $app -Force

        $nativeDll = Join-Path $root 'native\build\out\downcraft_native.dll'
        if (Test-Path $nativeDll) {
            Copy-Item $nativeDll $app -Force
        }
        else {
            Write-Host '   (native DLL missing - the managed fallback will be used)' -ForegroundColor Yellow
        }

        $zip = Join-Path $releaseRoot ("DownCraft-{0}-{1}{2}.zip" -f $Version, $Runtime, $(if ($SelfContained) { '' } else { '-lite' }))
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Optimal
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        return $zip
    }

    $packages = @()
    if (-not $NoSelfContained) {
        $packages += New-Package 'full' $true
    }

    if (-not $SkipLite) {
        $packages += New-Package 'lite' $false
    }

    $sums = foreach ($package in $packages) {
        $hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
        "{0}  {1}" -f $hash, (Split-Path $package -Leaf)
    }
    $sumFile = Join-Path $releaseRoot 'SHA256SUMS.txt'
    Set-Content -Path $sumFile -Value $sums -Encoding ASCII

    Write-Host ''
    Write-Host 'Release packages:' -ForegroundColor Green
    foreach ($package in $packages) {
        Write-Host ("  {0}  ({1:N1} MB)" -f $package, ((Get-Item $package).Length / 1MB))
    }
    Write-Host ("  {0}" -f $sumFile)
    Write-Host ''
    Write-Host 'Next: tag the repository and push, or upload the files manually:' -ForegroundColor DarkGray
    Write-Host ("  git tag v{0} && git push origin v{0}" -f $Version) -ForegroundColor DarkGray
    Write-Host ("  gh release create v{0} artifacts/release/*.zip artifacts/release/SHA256SUMS.txt --title ""DownCraft {0}""" -f $Version) -ForegroundColor DarkGray
    exit 0
}
catch {
    Write-Host ("Release failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
