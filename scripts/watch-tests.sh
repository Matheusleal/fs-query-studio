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
echo "==> [Watcher] Iniciando monitoramento externo de testes"
echo "    Comando:        $COMMAND"
echo "    Timeout limite: ${TIMEOUT_SECONDS}s ($(( TIMEOUT_SECONDS / 60 )) min)"
echo "    Heartbeat:      ${HEARTBEAT_SECONDS}s"
echo "======================================================================"
echo ""

# Iniciar o comando em background
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
        echo "[FATAL] [Watcher] A EXECUÇÃO EXCEDEU O LIMITE DE 5 MINUTOS (${TIMEOUT_SECONDS}s)!"
        echo "[FATAL] [Watcher] O teste está com problema (hang / deadlock / loop infinito)!"
        echo "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
        echo ""
        echo "[Watcher] Encerrando árvore de processos PID $PID..."
        
        # Kill toda a árvore de processos do PID
        pkill -P "$PID" 2>/dev/null || true
        kill -9 "$PID" 2>/dev/null || true
        
        exit 124
    fi

    MINUTES=$(( ELAPSED / 60 ))
    SECONDS=$(( ELAPSED % 60 ))
    printf "[Watcher] Execução em andamento... Decorrido: %02d:%02d / %02d:00\n" "$MINUTES" "$SECONDS" $(( TIMEOUT_SECONDS / 60 ))
done

wait "$PID"
EXIT_CODE=$?

END_TIME=$(date +%s)
TOTAL_ELAPSED=$(( END_TIME - START_TIME ))
TOTAL_MIN=$(( TOTAL_ELAPSED / 60 ))
TOTAL_SEC=$(( TOTAL_ELAPSED % 60 ))

if [ "$EXIT_CODE" -eq 0 ]; then
    printf "\n[OK] [Watcher] Testes concluídos com SUCESSO em %02d:%02d (ExitCode: 0).\n" "$TOTAL_MIN" "$TOTAL_SEC"
else
    printf "\n[ERROR] [Watcher] Testes FALHARAM em %02d:%02d com código de saída %d.\n" "$TOTAL_MIN" "$TOTAL_SEC" "$EXIT_CODE"
fi

exit "$EXIT_CODE"
