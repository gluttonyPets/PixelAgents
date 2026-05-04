#!/usr/bin/env bash
# run_tests.sh - Ejecuta los tests del proyecto PixelAgents usando Docker SDK .NET 8
# Uso: ./tools/run_tests.sh [--filter <pattern>]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FILTER="${2:-}"

echo "=== PixelAgents Test Runner ==="
echo "Repo: $REPO_ROOT"
echo ""

# Verificar si dotnet esta disponible localmente
if command -v dotnet &>/dev/null; then
    echo "[INFO] Usando dotnet local: $(dotnet --version)"
    CMD_PREFIX=""
else
    # Intentar con Docker si dotnet no esta disponible
    echo "[INFO] dotnet no encontrado localmente, intentando con Docker..."
    if ! command -v docker &>/dev/null; then
        echo "[ERROR] Ni dotnet ni docker estan disponibles en este sistema."
        echo "        Para ejecutar los tests, instala el SDK de .NET 8:"
        echo "        https://dotnet.microsoft.com/download/dotnet/8.0"
        echo "        O asegurate de que Docker este corriendo y ejecuta:"
        echo "        docker run --rm -v $REPO_ROOT:/src -w /src/Server.Tests mcr.microsoft.com/dotnet/sdk:8.0 dotnet test"
        exit 1
    fi

    echo "[INFO] Ejecutando tests via Docker SDK .NET 8..."
    FILTER_ARG=""
    if [ -n "$FILTER" ]; then
        FILTER_ARG="--filter $FILTER"
    fi

    docker run --rm \
        -v "$REPO_ROOT:/src" \
        -w /src/Server.Tests \
        mcr.microsoft.com/dotnet/sdk:8.0 \
        dotnet test \
            --logger "console;verbosity=normal" \
            --verbosity normal \
            $FILTER_ARG

    exit $?
fi

# Ejecucion local con dotnet
FILTER_ARG=""
if [ -n "$FILTER" ]; then
    FILTER_ARG="--filter $FILTER"
fi

cd "$REPO_ROOT/Server.Tests"
dotnet test \
    --logger "console;verbosity=normal" \
    --verbosity normal \
    $FILTER_ARG

echo ""
echo "=== Tests completados ==="
