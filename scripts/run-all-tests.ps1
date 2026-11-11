#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs all FSharpQueryStudio test projects under the supervision of the External Watcher (default 10 min timeout).
#>

[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 600,
    [switch]$SkipDocker,
    [switch]$SkipNpm
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir
$watcher = Join-Path $scriptDir "watch-tests.ps1"

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "==> Starting Full Test Suite with External Watcher" -ForegroundColor Cyan
Write-Host "    Timeout per Project: $TimeoutSeconds seconds ($([math]::Round($TimeoutSeconds / 60)) min)" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

$testProjects = @(
    "tests/FSharpQueryStudio.Protocol.Tests"
    "tests/FSharpQueryStudio.Logging.Tests"
    "tests/FSharpQueryStudio.Security.Tests"
    "tests/FSharpQueryStudio.Storage.Tests"
    "tests/FSharpQueryStudio.Results.Tests"
    "tests/FSharpQueryStudio.Connectors.Tests"
    "tests/FSharpQueryStudio.Fsac.Tests"
    "tests/FSharpQueryStudio.Execution.Tests"
    "tests/FSharpQueryStudio.Integration.Tests"
    "tests/FSharpQueryStudio.Worker.Tests"
)

# Pre-build the entire solution in Release mode
Write-Host "`n==> Building Solution in Release mode..." -ForegroundColor Yellow
& dotnet build "$rootDir/FSharpQueryStudio.slnx" -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "Solution build failed. Aborting test execution."
    exit 1
}

$swTotal = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($proj in $testProjects) {
    $projPath = Join-Path $rootDir $proj
    if (!(Test-Path $projPath)) {
        continue
    }

    $projName = Split-Path -Leaf $proj
    Write-Host "`n>>> [Watcher] Running test suite: $projName" -ForegroundColor Magenta

    $cmd = "dotnet test `"$projPath`" -c Release --no-build"
    if ($SkipDocker -and $proj -like "*Integration*") {
        $cmd += " --filter `"Category!=RequiresDocker`""
    }

    & pwsh -NoProfile -ExecutionPolicy Bypass -File $watcher -Command $cmd -TimeoutSeconds $TimeoutSeconds -HeartbeatSeconds 10
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Test suite '$projName' failed or exceeded the 5-minute timeout limit."
        exit $LASTEXITCODE
    }

    Start-Sleep -Seconds 2
}


if (!$SkipNpm) {
    $vscodeDir = Join-Path $rootDir "editors/vscode"
    if (Test-Path $vscodeDir) {
        Write-Host "`n>>> [Watcher] Running VS Code Extension tests (npm test, including sample_demo E2E)..." -ForegroundColor Magenta
        $releaseWorkerDll = Join-Path $rootDir "src/FSharpQueryStudio.Worker/bin/Release/net10.0/FSharpQueryStudio.Worker.dll"
        if (Test-Path $releaseWorkerDll) {
            $env:FSQS_WORKER_DLL = $releaseWorkerDll
        }
        $cmd = "npm test"
        Push-Location $vscodeDir
        try {
            & pwsh -NoProfile -ExecutionPolicy Bypass -File $watcher -Command $cmd -TimeoutSeconds $TimeoutSeconds -HeartbeatSeconds 5
            if ($LASTEXITCODE -ne 0) {
                Write-Error "VS Code extension tests (npm test) failed or exceeded the 5-minute timeout limit."
                exit $LASTEXITCODE
            }
        } finally {
            Pop-Location
        }
    }
}

$swTotal.Stop()
Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host "==> [OK] ALL TEST SUITES COMPLETED WITH 100% SUCCESS!" -ForegroundColor Green
Write-Host "    Total Elapsed Time: $($swTotal.Elapsed.ToString('mm\:ss'))" -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green
