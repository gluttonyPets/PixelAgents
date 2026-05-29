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

git pull origin main

export GIT_COMMIT=$(git rev-parse --short HEAD)
export BUILD_DATE=$(date -u '+%Y-%m-%d %H:%M:%S UTC')

echo ""
echo "🚀 Iniciando despliegue..."
echo "   Commit: $GIT_COMMIT"
echo "   Fecha: $BUILD_DATE"
echo "   URL: http://${PUBLIC_IP:-localhost}:${APP_PORT:-8080}"
echo ""

docker compose build --no-cache
docker compose up -d

echo ""
echo "✅ Despliegue completado"
echo "   URL: http://${PUBLIC_IP:-localhost}:${APP_PORT:-8080}"
echo "   Logs: docker compose logs -f pixelagents"
echo ""
