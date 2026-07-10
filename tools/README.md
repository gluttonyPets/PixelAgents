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

### `cleanup_stuck_executions.sql`

**Propósito:** Cerrar (marcar como `Cancelled`) ejecuciones "zombi" que se
quedaron en `Running`/`Waiting*` sin token de cancelación vivo (colgadas antes de
los arreglos de cancelación, o tras reiniciar el servidor). Aparecen como "En
curso" en la UI y el botón "Cancelar" no podía cerrarlas.

**Cuándo usar:**
- Para limpiar ejecuciones que ya estaban colgadas antes del fix del endpoint
  `/cancel` (a partir de ese fix, el botón las cierra solo).

**Uso:**

```bash
# Ejecutar en CADA base de datos de tenant por separado.
# Lanza primero el SELECT de preview que incluye el propio script.
psql "$TENANT_DB_URL" -f tools/cleanup_stuck_executions.sql
```

Incluye un guard de 30 minutos (`CreatedAt`) para no cancelar runs recién
lanzados que estén trabajando de verdad. Es idempotente.

---

### `migrate_remove_frozen_systemprompts.sql`

**Propósito:** Eliminar `systemPrompt` congelado en `ProjectModule.Configuration`
para que los cambios en `AiModule` surtan efecto. Ejecutar por tenant.

---

### Otros Scripts

*(Aquí se documentan los demás scripts conforme se agreguen)*

## Notas

- La mayoría de estos scripts están diseñados para ejecutarse desde la raíz del proyecto
- Algunos requieren que Docker esté corriendo
- Revisa la documentación individual de cada script para requisitos específicos
