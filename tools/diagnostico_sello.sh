#!/usr/bin/env bash
# Diagnostico del sello del build (el commit y la hora del pie del panel).
#
# Cuando el pie ensena un SHA que no corresponde, el valor puede venir de cuatro
# sitios distintos. Este script los enseña todos a la vez para no adivinar:
#
#   1. lo que responde la API en caliente;
#   2. el build-info.json que hay DENTRO del contenedor en marcha;
#   3. las variables GIT_COMMIT / BUILD_DATE del contenedor (un .env viejo las
#      cuela sin que nadie se entere);
#   4. la fecha del binario y de la imagen, que dicen si el contenedor se ha
#      reconstruido de verdad;
#   5. el estado del checkout del servidor, que es de donde sale el sello.
#
# Uso (en el servidor, dentro del directorio del proyecto):
#   ./tools/diagnostico_sello.sh

set -uo pipefail

cd "$(dirname "$0")/.."

SERVICE="${SERVICE:-pixelagents}"
[ -f .env ] && . ./.env 2>/dev/null
PORT="${APP_PORT:-8080}"

if docker compose version >/dev/null 2>&1; then
    DC="docker compose"
elif command -v docker-compose >/dev/null 2>&1; then
    DC="docker-compose"
else
    DC=""
fi

titulo() { printf '\n── %s ─────────────────────────────────\n' "$1"; }

titulo "1. Lo que responde la API"
curl -fsS "http://localhost:${PORT}/api/build-info" 2>&1 || echo "(no responde en el puerto ${PORT})"
echo

titulo "2. build-info.json dentro del contenedor"
if [ -n "$DC" ]; then
    $DC exec -T "$SERVICE" cat /app/build-info.json 2>&1 || echo "(no se ha podido leer)"
else
    echo "(no hay docker compose disponible)"
fi
echo

titulo "3. Variables del contenedor"
if [ -n "$DC" ]; then
    $DC exec -T "$SERVICE" sh -c 'echo "GIT_COMMIT=$GIT_COMMIT"; echo "BUILD_DATE=$BUILD_DATE"' 2>&1 \
        || echo "(no se ha podido leer)"
else
    echo "(no hay docker compose disponible)"
fi

titulo "4. Edad real de lo que se esta ejecutando"
if [ -n "$DC" ]; then
    echo -n "Server.dll en ejecucion: "
    $DC exec -T "$SERVICE" date -r /app/Server.dll -u '+%Y-%m-%d %H:%M:%S UTC' 2>&1 || echo "(no se ha podido leer)"
    CONTAINER="$($DC ps -q "$SERVICE" 2>/dev/null | head -1)"
    if [ -n "$CONTAINER" ]; then
        echo -n "Imagen creada:           "
        docker inspect -f '{{.Created}}' "$(docker inspect -f '{{.Image}}' "$CONTAINER")" 2>&1
        echo -n "Contenedor arrancado:    "
        docker inspect -f '{{.State.StartedAt}}' "$CONTAINER" 2>&1
    fi
fi

titulo "5. Checkout del servidor"
echo "HEAD local:    $(git rev-parse HEAD 2>&1)"
RAMA="${DEPLOY_BRANCH:-main}"
echo "origin/$RAMA: $(git rev-parse "origin/$RAMA" 2>&1)"
if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
    echo "OJO: hay cambios sin commitear en el servidor."
fi
if [ -f .env ] && grep -qE '^[[:space:]]*(GIT_COMMIT|BUILD_DATE)=' .env; then
    echo "OJO: .env define GIT_COMMIT o BUILD_DATE:"
    grep -nE '^[[:space:]]*(GIT_COMMIT|BUILD_DATE)=' .env
fi

printf '\n── Como leerlo ─────────────────────────────────\n'
cat <<'AYUDA'
- Si (1) y (2) coinciden pero no son el commit esperado -> la imagen se
  construyo con ese commit: el checkout del servidor no estaba donde tocaba.
- Si (3) trae un GIT_COMMIT distinto de (2) -> hay una variable pegada
  (normalmente en .env) tapando el sello de la imagen.
- Si en (4) la imagen es de hace dias -> el contenedor no se ha reconstruido,
  se esta ejecutando codigo viejo por mucho que el repositorio este al dia.
- Si en (5) HEAD no coincide con origin -> el servidor despliega otra cosa.
AYUDA
