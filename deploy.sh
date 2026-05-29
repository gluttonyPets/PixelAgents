#!/bin/bash
set -e

cd "$(dirname "$0")"

# Cargar variables de entorno si existe .env
if [ -f .env ]; then
    echo "📋 Cargando configuración desde .env..."
    set -a
    source .env
    set +a
else
    echo "⚠️  ADVERTENCIA: No se encontró archivo .env"
    echo "   Copia .env.example a .env y configura tus variables:"
    echo "   cp .env.example .env"
    echo ""
fi

# Validar configuración crítica para Buffer
if [ -z "$PUBLIC_IP" ] || [ "$PUBLIC_IP" = "localhost" ]; then
    echo ""
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "⚠️  ADVERTENCIA CRÍTICA: PUBLIC_IP no está configurado correctamente"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo ""
    echo "  PUBLIC_IP está configurado como: ${PUBLIC_IP:-'(vacío)'}"
    echo ""
    echo "  Si vas a usar Buffer para publicar en redes sociales"
    echo "  (Instagram, TikTok, etc.), DEBES configurar PUBLIC_IP"
    echo "  con tu IP pública o dominio accesible desde internet."
    echo ""
    echo "  Obtén tu IP pública con:"
    echo "    curl ifconfig.me"
    echo ""
    echo "  Luego edita .env y configura:"
    echo "    PUBLIC_IP=tu-ip-publica-aqui"
    echo ""
    echo "  Ejemplo:"
    echo "    PUBLIC_IP=123.45.67.89"
    echo "    PUBLIC_IP=midominio.com"
    echo ""
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo ""
    read -p "¿Deseas continuar de todos modos? (y/N) " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Despliegue cancelado."
        exit 1
    fi
fi

git pull origin main

export GIT_COMMIT=$(git rev-parse --short HEAD)
export BUILD_DATE=$(date -u '+%Y-%m-%d %H:%M:%S UTC')

echo ""
echo "🚀 Iniciando despliegue..."
echo "   Commit: $GIT_COMMIT"
echo "   Fecha: $BUILD_DATE"
echo "   URL pública: http://${PUBLIC_IP:-localhost}:${APP_PORT:-8080}"
echo ""

docker compose build --no-cache
docker compose up -d

echo ""
echo "✅ Despliegue completado"
echo "   Accede a la aplicación en: http://${PUBLIC_IP:-localhost}:${APP_PORT:-8080}"
echo "   Ver logs con: docker compose logs -f pixelagents"
echo ""
