#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Executa todos os projetos de teste do FSharpQueryStudio sob o monitoramento do Watcher Externo (5 min timeout).
#>

[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 300,
    [switch]$SkipDocker,
    [switch]$SkipNpm
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir
$watcher = Join-Path $scriptDir "watch-tests.ps1"

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "==> Iniciando Bateria Completa de Testes com Watcher Externo" -ForegroundColor Cyan
Write-Host "    Timeout por Projeto: $TimeoutSeconds segundos (5 min)" -ForegroundColor Cyan
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

# Compilação prévia da solução inteira
Write-Host "`n==> Compilando Solução em modo Release..." -ForegroundColor Yellow
& dotnet build "$rootDir/FSharpQueryStudio.slnx" -c Release
if ($LASTEXITCODE -ne 0) {

    Write-Error "Falha na compilação da solução. Abortando execução de testes."
    exit 1
}

$swTotal = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($proj in $testProjects) {
    $projPath = Join-Path $rootDir $proj
    if (!(Test-Path $projPath)) {
        continue
    }

    $projName = Split-Path -Leaf $proj
    Write-Host "`n>>> [Watcher] Executando suite: $projName" -ForegroundColor Magenta

    $cmd = "dotnet test `"$projPath`" -c Release --no-build"
    if ($SkipDocker -and $proj -like "*Integration*") {
        $cmd += " --filter `"Category!=RequiresDocker`""
    }

    & pwsh -NoProfile -ExecutionPolicy Bypass -File $watcher -Command $cmd -TimeoutSeconds $TimeoutSeconds -HeartbeatSeconds 10
    if ($LASTEXITCODE -ne 0) {
        Write-Error "A suite de testes '$projName' falhou ou excedeu o limite de 5 minutos."
        exit $LASTEXITCODE
    }

    Start-Sleep -Seconds 2
}


if (!$SkipNpm) {
    $vscodeDir = Join-Path $rootDir "editors/vscode"
    if (Test-Path $vscodeDir) {
        Write-Host "`n>>> [Watcher] Executando testes da Extensão VS Code (npm test)..." -ForegroundColor Magenta
        $cmd = "npm test"
        Push-Location $vscodeDir
        try {
            & pwsh -NoProfile -ExecutionPolicy Bypass -File $watcher -Command $cmd -TimeoutSeconds $TimeoutSeconds -HeartbeatSeconds 5
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Os testes do VS Code (npm test) falharam ou excederam o limite de 5 minutos."
                exit $LASTEXITCODE
            }
        } finally {
            Pop-Location
        }
    }
}

$swTotal.Stop()
Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host "==> [OK] TODAS AS SUÍTES DE TESTES FORAM CONCLUÍDAS COM 100% DE SUCESSO!" -ForegroundColor Green
Write-Host "    Tempo Total Decorrido: $($swTotal.Elapsed.ToString('mm\:ss'))" -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green
