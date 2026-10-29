#Requires -Version 5.1
<#
.SYNOPSIS
    Builds DownCraft: native helper (downcraft_native.dll), the .NET solution, and the unit tests.

.DESCRIPTION
    Run from anywhere:
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
        pwsh -File scripts/build.ps1 -Configuration Debug

.PARAMETER Configuration
    Debug or Release (default Release).

.PARAMETER SkipNative
    Skip the C++ helper. The product still works: the managed fallback for SHA-1/ZIP is used.

.PARAMETER SkipTests
    Skip `dotnet test`.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipNative,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    Write-Host "== DownCraft build ($Configuration) ==" -ForegroundColor Cyan

    if (-not $SkipNative) {
        Write-Host "-- 1/3 native helper (downcraft_native.dll)" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'build-native.ps1')
        if ($LASTEXITCODE -ne 0) {
            throw "native build failed with exit code $LASTEXITCODE"
        }
    }
    else {
        Write-Host "-- 1/3 native helper skipped" -ForegroundColor Yellow
    }

    Write-Host "-- 2/3 managed solution" -ForegroundColor Cyan
    dotnet build (Join-Path $root 'DownCraft.slnx') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    if (-not $SkipTests) {
        Write-Host "-- 3/3 unit tests" -ForegroundColor Cyan
        $testProject = Join-Path $root 'tests\DownCraft.Tests\DownCraft.Tests.csproj'
        $testApp = Join-Path $root "tests\DownCraft.Tests\bin\$Configuration\net10.0\DownCraft.Tests.exe"

        # xunit.v3 runs on Microsoft.Testing.Platform, so the test project is a normal
        # executable. Running it directly avoids the named-pipe handshake that `dotnet test`
        # needs (blocked by some antivirus / sandbox configurations) and always works.
        dotnet build $testProject -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "test build failed with exit code $LASTEXITCODE"
        }

        if (Test-Path $testApp) {
            & $testApp
            if ($LASTEXITCODE -ne 0) {
                throw "unit tests failed with exit code $LASTEXITCODE"
            }
        }
        else {
            dotnet test $testProject -c $Configuration --nologo
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet test failed with exit code $LASTEXITCODE"
            }
        }
    }
    else {
        Write-Host "-- 3/3 unit tests skipped" -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host 'Build succeeded.' -ForegroundColor Green
    Write-Host ("  CLI : {0}" -f (Join-Path $root "src\DownCraft.Cli\bin\$Configuration\net10.0\downcraft.exe"))
    Write-Host ("  GUI : {0}" -f (Join-Path $root "src\DownCraft.Gui\bin\$Configuration\net10.0-windows\DownCraft.Gui.exe"))
    Write-Host ("  DLL : {0}" -f (Join-Path $root 'native\build\out\downcraft_native.dll'))
    exit 0
}
catch {
    Write-Host ''
    Write-Host ("Build failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
