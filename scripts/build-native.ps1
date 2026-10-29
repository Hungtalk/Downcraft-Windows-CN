#Requires -Version 5.1
<#
.SYNOPSIS
    Builds mcdnative.dll and mcdl_native_test.exe with MSVC (x64, static CRT).

.DESCRIPTION
    Locates Visual Studio with vswhere.exe, initializes the x64 developer
    environment, then runs CMake + Ninja (or the newest installed Visual Studio
    generator when Ninja is unavailable). Outputs land in native/build/out.

.PARAMETER Clean
    Removes native/build before configuring.

.EXAMPLE
    pwsh -File scripts\build-native.ps1 -Clean
#>
[CmdletBinding()]
param(
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message"
}

function Fail {
    param([string]$Message)
    Write-Host ""
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$nativeDir = Join-Path $repoRoot 'native'
$buildDir = Join-Path $nativeDir 'build'
$dllPath = Join-Path $buildDir 'out\mcdnative.dll'
$testPath = Join-Path $buildDir 'out\mcdl_native_test.exe'

if (-not (Test-Path -LiteralPath (Join-Path $nativeDir 'CMakeLists.txt'))) {
    Fail "native/CMakeLists.txt was not found under '$repoRoot'."
}

if ($Clean) {
    if (Test-Path -LiteralPath $buildDir) {
        Write-Step "Cleaning $buildDir"
        try {
            Remove-Item -LiteralPath $buildDir -Recurse -Force -ErrorAction Stop
        }
        catch {
            Fail "Could not remove '$buildDir': $($_.Exception.Message)"
        }
    }
}

# ---------------------------------------------------------------------------
# Locate Visual Studio
# ---------------------------------------------------------------------------
$vsCandidates = New-Object System.Collections.Generic.List[string]
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswherePath) {
    Write-Step "Querying Visual Studio with vswhere"
    $queries = @(
        @('-latest', '-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath'),
        @('-latest', '-products', '*', '-property', 'installationPath'),
        @('-products', '*', '-property', 'installationPath')
    )
    foreach ($query in $queries) {
        $result = & $vswherePath @query 2>$null
        foreach ($line in @($result)) {
            if ($null -ne $line -and $line.Trim().Length -gt 0) {
                $vsCandidates.Add($line.Trim())
            }
        }
    }
}
else {
    Write-Host "    vswhere.exe not found at '$vswherePath'; probing standard install roots." -ForegroundColor Yellow
}

if ($env:ProgramFiles) {
    foreach ($major in @(18, 17)) {
        foreach ($edition in @('Community', 'Professional', 'Enterprise', 'BuildTools')) {
            $candidate = Join-Path $env:ProgramFiles "Microsoft Visual Studio\$major\$edition"
            if (Test-Path -LiteralPath $candidate) {
                $vsCandidates.Add($candidate)
            }
        }
    }
}

$vsRoot = $null
$vcvarsPath = $null
$vsdevPath = $null
foreach ($candidate in @($vsCandidates | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $candidate)) {
        continue
    }
    $vcvars = Join-Path $candidate 'VC\Auxiliary\Build\vcvars64.bat'
    $vsdev = Join-Path $candidate 'Common7\Tools\VsDevCmd.bat'
    $vcvarsUsable = $false
    if (Test-Path -LiteralPath $vcvars) {
        # VS 2026 installations can ship vcvars64.bat without vcvarsall.bat; in
        # that case the script falls back to VsDevCmd.bat below.
        $vcvarsUsable = Test-Path -LiteralPath (Join-Path (Split-Path -Parent $vcvars) 'vcvarsall.bat')
        if ($vcvarsUsable) {
            $vsRoot = $candidate
            $vcvarsPath = $vcvars
            $vsdevPath = $vsdev
            break
        }
    }
    if (Test-Path -LiteralPath $vsdev) {
        $vsRoot = $candidate
        $vcvarsPath = $vcvars
        $vsdevPath = $vsdev
        break
    }
}

if ($null -eq $vsRoot) {
    Fail "Visual Studio with the MSVC x64 toolset was not found. Install 'Desktop development with C++' or run this script from a Developer Command Prompt."
}

Write-Step "Visual Studio: $vsRoot"

# ---------------------------------------------------------------------------
# Locate CMake / Ninja
# ---------------------------------------------------------------------------
$cmakeCommand = Get-Command cmake.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $cmakeCommand) {
    Fail "cmake.exe was not found on PATH. Add CMake to PATH and retry."
}
$ninjaCommand = Get-Command ninja.exe -ErrorAction SilentlyContinue | Select-Object -First 1

$generatorArgs = @()
$generatorName = ''
if ($null -ne $ninjaCommand) {
    $generatorName = 'Ninja'
    $generatorArgs = @('-G', 'Ninja')
}
else {
    Write-Host "    Ninja not found; falling back to a Visual Studio generator." -ForegroundColor Yellow
    $helpText = (& $cmakeCommand.Source --help 2>&1 | Out-String)
    foreach ($candidate in @('Visual Studio 18 2026', 'Visual Studio 17 2022', 'Visual Studio 16 2019')) {
        if ($helpText.Contains($candidate)) {
            $generatorName = $candidate
            $generatorArgs = @('-G', $candidate, '-A', 'x64')
            break
        }
    }
    if ($generatorName.Length -eq 0) {
        Fail "Ninja is not available and CMake reports no supported Visual Studio generator."
    }
}
Write-Step "CMake generator: $generatorName"

# Quote generator tokens that contain spaces (batch syntax).
$generatorText = ($generatorArgs | ForEach-Object {
    if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
}) -join ' '

$toolPaths = @()
$toolPaths += (Split-Path -Parent $cmakeCommand.Source)
if ($null -ne $ninjaCommand) {
    $toolPaths += (Split-Path -Parent $ninjaCommand.Source)
}
$toolPathPrefix = (@($toolPaths | Select-Object -Unique) -join ';')

# ---------------------------------------------------------------------------
# Build through a temporary batch file so quoting is handled by cmd.exe itself.
# ---------------------------------------------------------------------------
$tempRoot = if ($env:TEMP) { $env:TEMP } else { $repoRoot }
$shimDir = Join-Path $tempRoot ("mcdl-build-" + [guid]::NewGuid().ToString('N'))
$shimPath = Join-Path $shimDir 'build.cmd'

function Convert-ToBatchLiteral {
    param([string]$Value)
    return $Value.Replace('%', '%%')
}

$batchRepo = Convert-ToBatchLiteral $repoRoot
$batchVcvars = if ($vcvarsPath) { Convert-ToBatchLiteral $vcvarsPath } else { '' }
$batchVsdev = if ($vsdevPath) { Convert-ToBatchLiteral $vsdevPath } else { '' }
$batchToolPath = Convert-ToBatchLiteral $toolPathPrefix

$shimLines = New-Object System.Collections.Generic.List[string]
$shimLines.Add('@echo off')
$shimLines.Add('setlocal EnableExtensions')
if ($batchVcvars.Length -gt 0) {
    $shimLines.Add("call `"$batchVcvars`" >nul 2>&1")
    $shimLines.Add('if errorlevel 1 goto mcdl_try_vsdevcmd')
    $shimLines.Add('goto mcdl_env_ready')
}
$shimLines.Add(':mcdl_try_vsdevcmd')
if ($batchVsdev.Length -gt 0) {
    $shimLines.Add("call `"$batchVsdev`" -arch=x64 -host_arch=x64 >nul 2>&1")
}
$shimLines.Add('if errorlevel 1 exit /b 10')
$shimLines.Add(':mcdl_env_ready')
# Force English MSVC diagnostics so Ninja can parse /showIncludes on localized
# installations (otherwise the whole include tree is dumped to the console).
$shimLines.Add('set "VSLANG=1033"')
$shimLines.Add('where cl.exe >nul 2>&1')
$shimLines.Add('if errorlevel 1 exit /b 11')
$shimLines.Add('if not defined VCToolsInstallDir exit /b 11')
# Some VS installs ship only the OneCore CRT import/static libraries. /MT needs
# libcmt.lib, so fall back to the OneCore library directory when the desktop one
# is absent.
$shimLines.Add('if exist "%VCToolsInstallDir%lib\x64\libcmt.lib" goto mcdl_crt_ready')
$shimLines.Add('if not exist "%VCToolsInstallDir%lib\onecore\x64\libcmt.lib" goto mcdl_crt_ready')
$shimLines.Add('set "LIB=%VCToolsInstallDir%lib\onecore\x64;%LIB%"')
$shimLines.Add(':mcdl_crt_ready')
if ($batchToolPath.Length -gt 0) {
    # Append rather than prepend: the WinGet tool directory also contains
    # MinGW's c++.exe, and prepending it would make CMake pick GCC over cl.exe.
    $shimLines.Add("set `"PATH=%PATH%;$batchToolPath`"")
}
$shimLines.Add("cd /d `"$batchRepo`"")
$shimLines.Add("cmake -S `"native`" -B `"native/build`" $generatorText -DCMAKE_BUILD_TYPE=Release")
$shimLines.Add('if errorlevel 1 exit /b 12')
$shimLines.Add('cmake --build "native/build" --config Release')
$shimLines.Add('if errorlevel 1 exit /b 13')
$shimLines.Add('exit /b 0')

$buildExit = 1
try {
    New-Item -ItemType Directory -Path $shimDir -Force | Out-Null
    Set-Content -LiteralPath $shimPath -Value ($shimLines -join "`r`n") -Encoding Default
    Write-Step "Configuring and building (static CRT, x64)"
    & cmd.exe /d /c $shimPath
    $buildExit = $LASTEXITCODE
}
catch {
    Fail "Failed to launch the native build: $($_.Exception.Message)"
}
finally {
    if (Test-Path -LiteralPath $shimDir) {
        Remove-Item -LiteralPath $shimDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($buildExit -ne 0) {
    $reason = switch ($buildExit) {
        10 { 'the MSVC x64 developer environment could not be initialized' }
        11 { 'cl.exe was not found after environment initialization' }
        12 { 'CMake configuration failed' }
        13 { 'the CMake build failed' }
        default { "the build command failed with exit code $buildExit" }
    }
    Fail "Native build failed: $reason. See the output above."
}

if (-not (Test-Path -LiteralPath $dllPath)) {
    Fail "The build reported success but '$dllPath' is missing."
}
if (-not (Test-Path -LiteralPath $testPath)) {
    Fail "The build reported success but '$testPath' is missing."
}

$dllSize = (Get-Item -LiteralPath $dllPath).Length
Write-Host ""
Write-Host "Native build succeeded." -ForegroundColor Green
Write-Host "  DLL : $dllPath ($dllSize bytes)"
Write-Host "  Test: $testPath"
exit 0
