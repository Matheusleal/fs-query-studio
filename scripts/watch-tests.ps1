<#
.SYNOPSIS
    Watcher externo para monitorar o tempo de execução de testes.
    Se a execução ultrapassar o tempo limite (padrão: 5 minutos / 300s),
    o processo e toda a sua árvore de processos filhos são terminados e um erro é reportado.

.PARAMETER Command
    O comando de teste a ser executado. Padrão: dotnet test FSharpQueryStudio.slnx -c Release --filter "Category!=RequiresDocker"

.PARAMETER TimeoutSeconds
    Tempo limite máximo em segundos. Padrão: 300 (5 minutos).

.PARAMETER HeartbeatSeconds
    Intervalo em segundos para emissão do status de progresso do watcher. Padrão: 5.
#>

[CmdletBinding()]
param(
    [string]$Command = 'dotnet test FSharpQueryStudio.slnx -c Release --filter "Category!=RequiresDocker"',
    [int]$TimeoutSeconds = 300,
    [int]$HeartbeatSeconds = 5
)

$ErrorActionPreference = 'Stop'

Write-Host "`n======================================================================" -ForegroundColor Cyan
Write-Host "==> [Watcher] Iniciando monitoramento externo de testes" -ForegroundColor Cyan
Write-Host "    Comando:       $Command" -ForegroundColor DarkGray
Write-Host "    Timeout limite: $TimeoutSeconds segundos ($([math]::Round($TimeoutSeconds / 60, 1)) min)" -ForegroundColor DarkGray
Write-Host "    Heartbeat:     $HeartbeatSeconds segundos" -ForegroundColor DarkGray
Write-Host "======================================================================`n" -ForegroundColor Cyan

function Stop-ProcessTree([int]$ParentId) {
    # Procura filhos recursivamente
    try {
        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ParentId" -ErrorAction SilentlyContinue
        foreach ($child in $children) {
            Stop-ProcessTree -ParentId $child.ProcessId
            try {
                Write-Host "    [Watcher] Encerrando subprocesso PID $($child.ProcessId) ($($child.Name))..." -ForegroundColor Yellow
                Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
            } catch { }
        }
    } catch { }

    try {
        Stop-Process -Id $ParentId -Force -ErrorAction SilentlyContinue
    } catch { }
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
if ($IsWindows -or $env:OS -like "*Windows*") {
    $psi.FileName = "cmd.exe"
    $psi.Arguments = "/c `"$Command`""
} else {
    $psi.FileName = "/bin/bash"
    $psi.Arguments = "-c `"$Command`""
}

$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $false
$psi.RedirectStandardError = $false

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$process = [System.Diagnostics.Process]::Start($psi)

if ($null -eq $process) {
    Write-Host "[ERROR] [Watcher] Falha ao iniciar o processo de teste." -ForegroundColor Red
    exit 1
}

$hasExited = $false
$timedOut = $false

while (-not $process.HasExited) {
    Start-Sleep -Seconds $HeartbeatSeconds
    $elapsed = $stopwatch.Elapsed

    if ($elapsed.TotalSeconds -ge $TimeoutSeconds) {
        $timedOut = $true
        break
    }

    $elapsedStr = "{0:D2}:{1:D2}" -f [int]$elapsed.TotalMinutes, $elapsed.Seconds
    $limitStr = "{0:D2}:{1:D2}" -f [int]($TimeoutSeconds / 60), ($TimeoutSeconds % 60)
    Write-Host "[Watcher] Execução em andamento... Decorrido: $elapsedStr / $limitStr" -ForegroundColor DarkCyan
}

$stopwatch.Stop()
$totalElapsed = $stopwatch.Elapsed
$totalElapsedStr = "{0:D2}:{1:D2}" -f [int]$totalElapsed.TotalMinutes, $totalElapsed.Seconds

if ($timedOut) {
    Write-Host "`n!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" -ForegroundColor Red
    Write-Host "[FATAL] [Watcher] A EXECUÇÃO EXCEDEU O LIMITE DE 5 MINUTOS ($TimeoutSeconds segundos)!" -ForegroundColor Red
    Write-Host "[FATAL] [Watcher] O teste está com problema (hang / deadlock / loop infinito)!" -ForegroundColor Red
    Write-Host "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!`n" -ForegroundColor Red
    Write-Host "[Watcher] Encerrando árvore de processos PID $($process.Id)..." -ForegroundColor Yellow

    Stop-ProcessTree -ParentId $process.Id

    exit 124
}

$exitCode = $process.ExitCode
if ($exitCode -eq 0) {
    Write-Host "`n[OK] [Watcher] Testes concluídos com SUCESSO em $totalElapsedStr (ExitCode: 0)." -ForegroundColor Green
} else {
    Write-Host "`n[ERROR] [Watcher] Testes FALHARAM em $totalElapsedStr com código de saída $exitCode." -ForegroundColor Red
}

exit $exitCode
