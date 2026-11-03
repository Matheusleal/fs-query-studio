#!/usr/bin/env bash
# ==============================================================================
# watch-tests.sh - External test watcher with strict 5-minute timeout.
# If execution exceeds 5 minutes, processes are killed and an alert is reported.
# ==============================================================================

set -u

COMMAND="${1:-dotnet test FSharpQueryStudio.slnx -c Release --filter "Category!=RequiresDocker"}"
TIMEOUT_SECONDS="${2:-300}"
HEARTBEAT_SECONDS="${3:-5}"

echo ""
echo "======================================================================"
echo "==> [Watcher] Starting external test execution monitor"
echo "    Command:        $COMMAND"
echo "    Timeout limit:  ${TIMEOUT_SECONDS}s ($(( TIMEOUT_SECONDS / 60 )) min)"
echo "    Heartbeat:      ${HEARTBEAT_SECONDS}s"
echo "======================================================================"
echo ""

# Start the command in the background
eval "$COMMAND" &
PID=$!

START_TIME=$(date +%s)

while kill -0 "$PID" 2>/dev/null; do
    sleep "$HEARTBEAT_SECONDS"
    NOW=$(date +%s)
    ELAPSED=$(( NOW - START_TIME ))

    if [ "$ELAPSED" -ge "$TIMEOUT_SECONDS" ]; then
        echo ""
        echo "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
        echo "[FATAL] [Watcher] EXECUTION EXCEEDED THE 5-MINUTE LIMIT (${TIMEOUT_SECONDS}s)!"
        echo "[FATAL] [Watcher] The test suite has a problem (hang / deadlock / infinite loop)!"
        echo "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
        echo ""
        echo "[Watcher] Terminating process tree for PID $PID..."
        
        # Kill the entire process tree for PID
        pkill -P "$PID" 2>/dev/null || true
        kill -9 "$PID" 2>/dev/null || true
        
        exit 124
    fi

    MINUTES=$(( ELAPSED / 60 ))
    SECONDS=$(( ELAPSED % 60 ))
    printf "[Watcher] Test execution in progress... Elapsed: %02d:%02d / %02d:00\n" "$MINUTES" "$SECONDS" $(( TIMEOUT_SECONDS / 60 ))
done

wait "$PID"
EXIT_CODE=$?

END_TIME=$(date +%s)
TOTAL_ELAPSED=$(( END_TIME - START_TIME ))
TOTAL_MIN=$(( TOTAL_ELAPSED / 60 ))
TOTAL_SEC=$(( TOTAL_ELAPSED % 60 ))

if [ "$EXIT_CODE" -eq 0 ]; then
    printf "\n[OK] [Watcher] Tests completed SUCCESSFULLY in %02d:%02d (ExitCode: 0).\n" "$TOTAL_MIN" "$TOTAL_SEC"
else
    printf "\n[ERROR] [Watcher] Tests FAILED in %02d:%02d with exit code %d.\n" "$TOTAL_MIN" "$TOTAL_SEC" "$EXIT_CODE"
fi

exit "$EXIT_CODE"
