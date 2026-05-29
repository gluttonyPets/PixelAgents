#!/bin/bash
# Script para diagnosticar y arreglar el webhook de Telegram

set -e

echo "🔧 PixelAgents - Telegram Webhook Fixer"
echo "========================================"
echo ""

# Verificar que se proporcione el bot token
if [ -z "$1" ]; then
    echo "Uso: $0 <BOT_TOKEN> [PUBLIC_URL]"
    echo ""
    echo "Ejemplo:"
    echo "  $0 123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11"
    echo "  $0 123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11 http://51.75.26.236:8080"
    echo ""
    exit 1
fi

BOT_TOKEN="$1"
PUBLIC_URL="${2:-}"

# Si no se proporciona URL pública, intentar detectarla
if [ -z "$PUBLIC_URL" ]; then
    if [ -f .env ]; then
        source .env
        PUBLIC_URL="http://${PUBLIC_IP}:${APP_PORT:-8080}"
        echo "📋 URL detectada desde .env: $PUBLIC_URL"
    else
        echo "⚠️  No se proporcionó URL pública y no se encontró .env"
        echo "   Usando la IP del servidor..."
        PUBLIC_IP=$(curl -s ifconfig.me || echo "localhost")
        PUBLIC_URL="http://${PUBLIC_IP}:8080"
        echo "📋 URL detectada: $PUBLIC_URL"
    fi
fi

WEBHOOK_URL="${PUBLIC_URL}/api/webhooks/telegram"

echo ""
echo "🔍 Verificando webhook actual..."
echo ""

# Obtener información del webhook actual
WEBHOOK_INFO=$(curl -s "https://api.telegram.org/bot${BOT_TOKEN}/getWebhookInfo")

# Mostrar webhook actual
echo "$WEBHOOK_INFO" | python3 -m json.tool || echo "$WEBHOOK_INFO"

# Extraer la URL actual
CURRENT_URL=$(echo "$WEBHOOK_INFO" | python3 -c "import sys, json; data=json.load(sys.stdin); print(data.get('result', {}).get('url', '(ninguna)'))" 2>/dev/null || echo "Error al parsear")

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  URL actual del webhook:  $CURRENT_URL"
echo "  URL correcta debería ser: $WEBHOOK_URL"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""

if [ "$CURRENT_URL" = "$WEBHOOK_URL" ]; then
    echo "✅ El webhook YA está configurado correctamente."
    echo ""
    echo "Si Telegram no está funcionando, verifica:"
    echo "  1. Que el puerto ${APP_PORT:-8080} esté abierto en el firewall"
    echo "  2. Que la aplicación esté corriendo: docker compose ps"
    echo "  3. Los logs del servidor: docker compose logs pixelagents --tail=100"
    exit 0
fi

# Preguntar si actualizar
echo "El webhook está apuntando a una URL incorrecta."
echo ""
read -p "¿Deseas actualizar el webhook a la URL correcta? (y/N) " -n 1 -r
echo

if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Operación cancelada."
    exit 0
fi

echo ""
echo "🔄 Actualizando webhook..."

# Actualizar el webhook
RESPONSE=$(curl -s -X POST "https://api.telegram.org/bot${BOT_TOKEN}/setWebhook" \
    -H "Content-Type: application/json" \
    -d "{\"url\": \"${WEBHOOK_URL}\"}")

echo "$RESPONSE" | python3 -m json.tool || echo "$RESPONSE"

# Verificar si fue exitoso
if echo "$RESPONSE" | grep -q '"ok":true'; then
    echo ""
    echo "✅ Webhook actualizado exitosamente!"
    echo ""
    echo "Nueva URL: $WEBHOOK_URL"
    echo ""
    echo "Ahora prueba la interacción de Telegram de nuevo."
else
    echo ""
    echo "❌ Error al actualizar el webhook."
    echo "Verifica que el bot token sea correcto."
    exit 1
fi
