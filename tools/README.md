# PixelAgents - Herramientas y Scripts de Utilidad

Esta carpeta contiene scripts y herramientas auxiliares para la administración y mantenimiento de PixelAgents.

## Scripts Disponibles

### `fix_telegram_webhook.sh`

**Propósito:** Diagnosticar y reparar problemas con el webhook de Telegram después de cambiar la configuración de URL pública.

**Cuándo usar:**
- Después de configurar o cambiar `PUBLIC_IP` en `.env`
- Cuando Telegram no recibe respuestas del usuario (pipeline se queda en "WaitingForInput")
- Para verificar la configuración actual del webhook de Telegram

**Uso:**

```bash
# Modo automático (detecta URL desde .env)
./tools/fix_telegram_webhook.sh TU_BOT_TOKEN

# Modo manual (especifica la URL pública)
./tools/fix_telegram_webhook.sh TU_BOT_TOKEN http://51.75.26.236:8080
```

**Ejemplo:**

```bash
cd /ruta/a/PixelAgents
./tools/fix_telegram_webhook.sh 123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11
```

El script:
1. Verifica la configuración actual del webhook
2. Compara con la URL que debería tener
3. Ofrece actualizar el webhook si es necesario
4. Confirma el resultado

---

### Otros Scripts

*(Aquí se documentan los demás scripts conforme se agreguen)*

## Notas

- La mayoría de estos scripts están diseñados para ejecutarse desde la raíz del proyecto
- Algunos requieren que Docker esté corriendo
- Revisa la documentación individual de cada script para requisitos específicos
