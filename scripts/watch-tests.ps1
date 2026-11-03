<#
.SYNOPSIS
    External watcher to monitor test execution time.
    If the execution exceeds the maximum timeout (default: 5 minutes / 300s),
    the process and its entire child process tree are terminated and a fatal error is reported.

.PARAMETER Command
    The test command to execute. Default: dotnet test FSharpQueryStudio.slnx -c Release --filter "Category!=RequiresDocker"

.PARAMETER TimeoutSeconds
    Maximum allowed execution time in seconds. Default: 300 (5 minutes).

.PARAMETER HeartbeatSeconds
    Interval in seconds for emitting progress heartbeat messages. Default: 5.
#>

[CmdletBinding()]
param(
    [string]$Command = 'dotnet test FSharpQueryStudio.slnx -c Release --filter "Category!=RequiresDocker"',
    [int]$TimeoutSeconds = 300,
    [int]$HeartbeatSeconds = 5
)

$ErrorActionPreference = 'Stop'

Write-Host "`n======================================================================" -ForegroundColor Cyan
Write-Host "==> [Watcher] Starting external test execution monitor" -ForegroundColor Cyan
Write-Host "    Command:        $Command" -ForegroundColor DarkGray
Write-Host "    Timeout limit:  $TimeoutSeconds seconds ($([math]::Round($TimeoutSeconds / 60, 1)) min)" -ForegroundColor DarkGray
Write-Host "    Heartbeat:      $HeartbeatSeconds seconds" -ForegroundColor DarkGray
Write-Host "======================================================================`n" -ForegroundColor Cyan

function Stop-ProcessTree([int]$ParentId) {
    # Recursively find and terminate child processes
    try {
        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ParentId" -ErrorAction SilentlyContinue
        foreach ($child in $children) {
            Stop-ProcessTree -ParentId $child.ProcessId
            try {
                Write-Host "    [Watcher] Terminating child process PID $($child.ProcessId) ($($child.Name))..." -ForegroundColor Yellow
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
    Write-Host "[ERROR] [Watcher] Failed to start the test process." -ForegroundColor Red
    exit 1
}

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
    Write-Host "[Watcher] Test execution in progress... Elapsed: $elapsedStr / $limitStr" -ForegroundColor DarkCyan
}

$stopwatch.Stop()
$totalElapsed = $stopwatch.Elapsed
$totalElapsedStr = "{0:D2}:{1:D2}" -f [int]$totalElapsed.TotalMinutes, $totalElapsed.Seconds

if ($timedOut) {
    Write-Host "`n!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" -ForegroundColor Red
    Write-Host "[FATAL] [Watcher] EXECUTION EXCEEDED THE 5-MINUTE LIMIT ($TimeoutSeconds seconds)!" -ForegroundColor Red
    Write-Host "[FATAL] [Watcher] The test suite has a problem (hang / deadlock / infinite loop)!" -ForegroundColor Red
    Write-Host "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!`n" -ForegroundColor Red
    Write-Host "[Watcher] Terminating process tree for PID $($process.Id)..." -ForegroundColor Yellow

    Stop-ProcessTree -ParentId $process.Id

    exit 124
}

$exitCode = $process.ExitCode
if ($exitCode -eq 0) {
    Write-Host "`n[OK] [Watcher] Tests completed SUCCESSFULLY in $totalElapsedStr (ExitCode: 0)." -ForegroundColor Green
} else {
    Write-Host "`n[ERROR] [Watcher] Tests FAILED in $totalElapsedStr with exit code $exitCode." -ForegroundColor Red
}

exit $exitCode
