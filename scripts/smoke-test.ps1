#Requires -Version 5.1
<#
.SYNOPSIS
    End-to-end smoke test: installs one version with the CLI, validates the produced
    .minecraft layout, then runs the verify command over it.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
    pwsh -File scripts/smoke-test.ps1 -Loader fabric -Version 1.21.1
    pwsh -File scripts/smoke-test.ps1 -Loader forge -WithAssets     # slow: assets + installer

.NOTES
    Requires a previous build (scripts\build.ps1). Uses a scratch directory inside the repo,
    never touches a real .minecraft folder.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.20.1',
    [ValidateSet('vanilla', 'fabric', 'quilt', 'forge', 'neoforge')]
    [string]$Loader = 'vanilla',
    [string]$LoaderVersion = '',
    [switch]$WithAssets,
    [string]$Dir = '',
    [string]$CliPath = '',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $CliPath) {
    $candidate = Join-Path $root "src\DownCraft.Cli\bin\$Configuration\net10.0\downcraft.dll"
    if (-not (Test-Path $candidate)) {
        throw "downcraft.dll not found: $candidate. Run scripts\build.ps1 first."
    }
    $CliPath = $candidate
}

if (-not $Dir) {
    $Dir = Join-Path $root (".scratch\smoke-" + $Loader)
}

if (Test-Path $Dir) {
    Write-Host ("cleaning {0}" -f $Dir) -ForegroundColor DarkGray
    Remove-Item $Dir -Recurse -Force
}

$failures = New-Object System.Collections.Generic.List[string]
function Assert-True([bool]$condition, [string]$message) {
    if ($condition) {
        Write-Host ("  [ok]   {0}" -f $message) -ForegroundColor Green
    }
    else {
        Write-Host ("  [FAIL] {0}" -f $message) -ForegroundColor Red
        $script:failures.Add($message)
    }
}

function Invoke-Mcdl([string[]]$arguments) {
    $all = @($CliPath) + $arguments
    Write-Host ("  > downcraft {0}" -f ($arguments -join ' ')) -ForegroundColor DarkGray
    # Out-Host keeps the child output off this function's return pipeline.
    & dotnet @all 2>&1 | Out-Host
    return $LASTEXITCODE
}

Write-Host ("== smoke test: Minecraft {0} / {1} ==" -f $Version, $Loader) -ForegroundColor Cyan

$installArgs = @('install', $Version, '-d', $Dir, '--loader', $Loader, '-j', '16')
if ($LoaderVersion) { $installArgs += @('--loader-version', $LoaderVersion) }
if (-not $WithAssets) { $installArgs += '--no-assets' }

$code = Invoke-Mcdl $installArgs
Assert-True ($code -eq 0) "install exited with 0 (got $code)"

# --- layout invariants -------------------------------------------------------
Assert-True (Test-Path (Join-Path $Dir 'libraries')) 'libraries/ exists'
Assert-True (Test-Path (Join-Path $Dir 'assets\indexes')) 'assets/indexes/ exists'
Assert-True (Test-Path (Join-Path $Dir 'launcher_profiles.json')) 'launcher_profiles.json exists'

$profilePath = Join-Path $Dir 'launcher_profiles.json'
if (Test-Path $profilePath) {
    $profile = Get-Content $profilePath -Raw | ConvertFrom-Json
    $lastIds = @($profile.profiles.PSObject.Properties | ForEach-Object { $_.Value.lastVersionId })
    Assert-True ($lastIds.Count -ge 1) 'launcher_profiles.json contains at least one profile'
}

$versionDirs = @()
if (Test-Path (Join-Path $Dir 'versions')) {
    $versionDirs = @(Get-ChildItem (Join-Path $Dir 'versions') -Directory | Select-Object -ExpandProperty Name)
}
Assert-True ($versionDirs -contains $Version) "versions/$Version/ exists"

$vanillaJson = Join-Path $Dir ("versions\{0}\{0}.json" -f $Version)
Assert-True (Test-Path $vanillaJson) "versions/$Version/$Version.json exists"

if ($Loader -eq 'vanilla') {
    $jar = Join-Path $Dir ("versions\{0}\{0}.jar" -f $Version)
    Assert-True (Test-Path $jar) "versions/$Version/$Version.jar exists"
    if (Test-Path $jar) {
        Assert-True ((Get-Item $jar).Length -gt 1MB) 'client jar is larger than 1 MB'
    }
}
else {
    $loaderDir = $versionDirs | Where-Object { $_ -ne $Version -and ($_ -match $Loader -or $Loader -eq 'neoforge') } | Select-Object -First 1
    Assert-True ($null -ne $loaderDir) "a loader version directory was created ($loaderDir)"
    if ($loaderDir) {
        $loaderJson = Join-Path $Dir ("versions\{0}\{0}.json" -f $loaderDir)
        Assert-True (Test-Path $loaderJson) "versions/$loaderDir/$loaderDir.json exists"
    }
}

$libraryCount = 0
if (Test-Path (Join-Path $Dir 'libraries')) {
    $libraryCount = @(Get-ChildItem (Join-Path $Dir 'libraries') -Recurse -File).Count
}
Assert-True ($libraryCount -gt 10) "libraries/ contains jars ($libraryCount files)"

if ($WithAssets) {
    $assetCount = 0
    $objectsDir = Join-Path $Dir 'assets\objects'
    if (Test-Path $objectsDir) {
        $assetCount = @(Get-ChildItem $objectsDir -Recurse -File).Count
    }
    Assert-True ($assetCount -gt 100) "assets/objects/ contains files ($assetCount objects)"
}

# --- verify pass -------------------------------------------------------------
$verifyArgs = @('verify', $Version, '-d', $Dir, '--loader', $Loader)
if ($LoaderVersion) { $verifyArgs += @('--loader-version', $LoaderVersion) }
$code = Invoke-Mcdl $verifyArgs
Assert-True ($code -eq 0) "verify exited with 0 (got $code)"

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host ("SMOKE TEST PASSED — {0}" -f $Dir) -ForegroundColor Green
    exit 0
}

Write-Host ("SMOKE TEST FAILED ({0} checks)" -f $failures.Count) -ForegroundColor Red
foreach ($failure in $failures) {
    Write-Host ("  - {0}" -f $failure) -ForegroundColor Red
}
exit 1
