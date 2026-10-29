#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the CLI and the WPF GUI into artifacts\publish.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
    pwsh -File scripts/publish.ps1 -SelfContained -SingleFile

.NOTES
    Self-contained publishes do not need the .NET 10 runtime on the target machine.
    PublishSingleFile keeps downcraft_native.dll next to the executable (it is loaded via P/Invoke).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$SingleFile,
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    $cli = Join-Path $root 'src\DownCraft.Cli\DownCraft.Cli.csproj'
    $gui = Join-Path $root 'src\DownCraft.Gui\DownCraft.Gui.csproj'
    $cliOut = Join-Path $Output 'cli'
    $guiOut = Join-Path $Output 'gui'

    $common = @(
        '-c', $Configuration,
        '-r', $Runtime,
        "--self-contained", $SelfContained.IsPresent.ToString().ToLowerInvariant(),
        '--nologo'
    )
    if ($SingleFile) {
        $common += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
    }

    New-Item -ItemType Directory -Force -Path $cliOut, $guiOut | Out-Null

    Write-Host '== publish CLI ==' -ForegroundColor Cyan
    dotnet publish $cli @common -o $cliOut
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed ($LASTEXITCODE)" }

    Write-Host '== publish GUI ==' -ForegroundColor Cyan
    dotnet publish $gui @common -o $guiOut
    if ($LASTEXITCODE -ne 0) { throw "GUI publish failed ($LASTEXITCODE)" }

    Write-Host ''
    Write-Host 'Publish succeeded.' -ForegroundColor Green
    Write-Host ("  CLI : {0}" -f (Join-Path $cliOut 'downcraft.exe'))
    Write-Host ("  GUI : {0}" -f (Join-Path $guiOut 'DownCraft.Gui.exe'))
    Write-Host ("  DLL : {0}" -f (Join-Path (Join-Path $root 'native\build\out') 'downcraft_native.dll'))
    exit 0
}
catch {
    Write-Host ("Publish failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
