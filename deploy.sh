#!/bin/bash
set -e

cd "$(dirname "$0")"

# Cargar variables de entorno si existe .env
if [ -f .env ]; then
    echo "📋 Cargando configuración desde .env..."
    set -a
    source .env
    set +a
fi

# Validar configuración crítica para Buffer (solo advertencia, no bloquea el deploy)
if [ -z "$PUBLIC_IP" ] || [ "$PUBLIC_IP" = "localhost" ]; then
    echo ""
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "⚠️  ADVERTENCIA: PUBLIC_IP no está configurado correctamente"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo ""
    echo "  PUBLIC_IP: ${PUBLIC_IP:-'(vacío)'}"
    echo ""
    echo "  Si usas Buffer para publicar en redes sociales, necesitas"
    echo "  configurar PUBLIC_IP con tu IP pública o dominio."
    echo ""
    echo "  Ver: docs/BUFFER_SETUP.md para más información"
    echo ""
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo ""
fi

# Rama que se despliega. Configurable por si el servidor sigue otra distinta.
DEPLOY_BRANCH="${DEPLOY_BRANCH:-main}"

git pull origin "$DEPLOY_BRANCH"

# ── Sello del build ────────────────────────────────────────────────────────
# El SHA que se sella es el de HEAD de ESTE checkout, que no tiene por que
# coincidir con lo que hay en GitHub: si el servidor arrastra commits locales,
# un merge del propio `git pull` o cambios sin commitear, el pie acababa
# mostrando un hash que no existe en el repositorio y no habia forma de
# rastrearlo. Cuando pasa, se marca en el propio sello en vez de mentir.
GIT_COMMIT="$(git rev-parse --short=7 HEAD)"

if git rev-parse --verify --quiet "origin/${DEPLOY_BRANCH}" >/dev/null \
   && ! git merge-base --is-ancestor HEAD "origin/${DEPLOY_BRANCH}"; then
    GIT_COMMIT="${GIT_COMMIT}-local"
    echo ""
    echo "⚠️  Este checkout tiene commits que NO estan en origin/${DEPLOY_BRANCH}."
    echo "    HEAD:                    $(git rev-parse HEAD)"
    echo "    origin/${DEPLOY_BRANCH}: $(git rev-parse "origin/${DEPLOY_BRANCH}" 2>/dev/null || echo '(desconocido)')"
    echo "    El sello se marca como '-local': lo desplegado no es lo que hay en GitHub."
fi

if ! git diff --quiet || ! git diff --cached --quiet; then
    GIT_COMMIT="${GIT_COMMIT}-sucio"
    echo ""
    echo "⚠️  Hay cambios sin commitear en el servidor; el sello se marca como '-sucio'."
fi

export GIT_COMMIT
# En UTC e ISO-8601: el servidor lo convierte a hora de Madrid al mostrarlo.
export BUILD_DATE=$(date -u '+%Y-%m-%dT%H:%M:%SZ')

# `docker compose` lee .env por su cuenta: si ahi hay un GIT_COMMIT o un
# BUILD_DATE viejos, se colarian en el contenedor y volverian a falsear el pie.
if [ -f .env ] && grep -qE '^[[:space:]]*(GIT_COMMIT|BUILD_DATE)=' .env; then
    echo ""
    echo "⚠️  .env define GIT_COMMIT o BUILD_DATE. Quitalos: el sello lo calcula este script."
fi

echo ""
echo "🚀 Iniciando despliegue..."
echo "   Commit: $GIT_COMMIT"
echo "   Fecha: $BUILD_DATE"
echo "   URL: http://${PUBLIC_IP:-localhost}:${APP_PORT:-8080}"
echo ""

# ── Limpieza de Docker ─────────────────────────────────────────────────────
# Cada `build --no-cache` deja la imagen anterior huerfana (<none>) y suma capas
# a la cache de build, que con --no-cache nunca se reutiliza. Sin limpiar, el
# disco se llena y el siguiente build falla. Solo se borran imagenes huerfanas y
# cache de build: NUNCA volumenes (pgdata = base de datos, media = archivos).
limpiar_docker() {
    docker image prune -f >/dev/null || true
    docker builder prune -af >/dev/null || true
}

echo "🧹 Liberando espacio antes del build..."
limpiar_docker
df -h / | tail -1 | awk '{print "   Disco libre: " $4 " de " $2}'

docker compose build --no-cache
docker compose up -d

# La imagen que se estaba ejecutando pasa a huerfana al arrancar la nueva.
echo "🧹 Borrando la imagen anterior..."
limpiar_docker
df -h / | tail -1 | awk '{print "   Disco libre: " $4 " de " $2}'

echo ""
echo "✅ Despliegue completado"
echo "   URL: http://${PUBLIC_IP:-localhost}:${APP_PORT:-8080}"
echo "   Logs: docker compose logs -f pixelagents"
echo ""
