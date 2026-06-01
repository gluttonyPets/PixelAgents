#!/bin/bash

# Script para probar que el endpoint buffer-image responde a solicitudes HEAD
# Uso: ./test_buffer_head_request.sh <URL_BASE> <SLOT>
# Ejemplo: ./test_buffer_head_request.sh https://tu-dominio.com 1

URL_BASE=${1:-"http://localhost:5000"}
SLOT=${2:-1}

ENDPOINT="${URL_BASE}/api/public/buffer-image/${SLOT}"

echo "======================================"
echo "Probando endpoint de Buffer con HEAD"
echo "======================================"
echo ""
echo "URL: ${ENDPOINT}"
echo ""

echo "1. Probando solicitud HEAD (lo que Buffer usa para validar):"
echo "------------------------------------------------------"
curl -I -X HEAD "${ENDPOINT}"
RESULT=$?

echo ""
echo "2. Verificando código de respuesta:"
echo "------------------------------------------------------"
HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X HEAD "${ENDPOINT}")
echo "Código HTTP: ${HTTP_CODE}"

if [ "$HTTP_CODE" == "200" ]; then
    echo "✅ ¡Éxito! El endpoint responde correctamente a HEAD"
elif [ "$HTTP_CODE" == "404" ]; then
    echo "⚠️  Slot no encontrado (404) - esto es normal si el slot está vacío"
elif [ "$HTTP_CODE" == "405" ]; then
    echo "❌ Error 405 - El endpoint aún NO soporta HEAD"
else
    echo "⚠️  Código inesperado: ${HTTP_CODE}"
fi

echo ""
echo "3. Probando solicitud GET (para comparar):"
echo "------------------------------------------------------"
HTTP_CODE_GET=$(curl -s -o /dev/null -w "%{http_code}" -X GET "${ENDPOINT}")
echo "Código HTTP GET: ${HTTP_CODE_GET}"

echo ""
echo "======================================"
echo "Resumen:"
echo "======================================"
echo "HEAD: ${HTTP_CODE}"
echo "GET:  ${HTTP_CODE_GET}"

if [ "$HTTP_CODE" == "$HTTP_CODE_GET" ]; then
    echo "✅ Ambos métodos devuelven el mismo código (correcto)"
else
    echo "⚠️  Los códigos difieren - revisar implementación"
fi
