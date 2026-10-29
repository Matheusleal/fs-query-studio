#!/usr/bin/env bash
set -eo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
WATCHER="$SCRIPT_DIR/watch-tests.sh"
TIMEOUT=${TIMEOUT:-300}

echo "======================================================================"
echo "==> Iniciando Bateria Completa de Testes com Watcher Externo (Linux/Docker)"
echo "    Timeout por Projeto: ${TIMEOUT}s (5 min)"
echo "======================================================================"

dotnet build "$ROOT_DIR/FSharpQueryStudio.slnx" -c Release


TEST_PROJECTS=(
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

for proj in "${TEST_PROJECTS[@]}"; do
    PROJ_PATH="$ROOT_DIR/$proj"
    if [ -d "$PROJ_PATH" ]; then
        echo -e "\n>>> [Watcher] Executando suite: $(basename "$proj")"
        EXTRA_ARGS=""
        if [ "$(basename "$proj")" = "FSharpQueryStudio.Integration.Tests" ] && [ -f /.dockerenv ]; then
            echo "    [Nota] Executando dentro de container Docker; excluindo testes que requerem Testcontainers aninhado (--TestCaseFilter Category!=RequiresDocker)"
            EXTRA_ARGS="--TestCaseFilter Category!=RequiresDocker"
        fi
        PROJ_NAME="$(basename "$proj")"
        DLL_PATH="$PROJ_PATH/bin/Release/net10.0/$PROJ_NAME.dll"
        if [ -f "$DLL_PATH" ]; then
            CMD="dotnet test \"$DLL_PATH\" --logger \"console;verbosity=normal\" $EXTRA_ARGS"
        else
            CMD="dotnet test \"$PROJ_PATH\" -c Release --no-build --logger \"console;verbosity=normal\" $EXTRA_ARGS"
        fi
        bash "$WATCHER" "$CMD" "$TIMEOUT" 10
    fi
done

if [ -d "$ROOT_DIR/editors/vscode" ]; then
    if command -v npm >/dev/null 2>&1; then
        echo -e "\n>>> [Watcher] Executando testes da Extensão VS Code (npm test)..."
        cd "$ROOT_DIR/editors/vscode"
        bash "$WATCHER" "npm test" "$TIMEOUT" 5
        cd "$ROOT_DIR"
    else
        echo -e "\n>>> [Watcher] Node/npm não encontrado no ambiente atual. Pulando testes da extensão VS Code."
    fi
fi

echo "======================================================================"
echo "==> [OK] TODAS AS SUÍTES DE TESTES FORAM CONCLUÍDAS COM 100% DE SUCESSO!"
echo "======================================================================"
