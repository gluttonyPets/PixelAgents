# Fix: publicación / interacción enviada dos veces en el chat de Telegram

> Estado: **resuelto** · Fecha: 2026-07-06 · Área: `Server/Services/Telegram/TelegramUpdateHandler.cs`, `Server/Services/Scheduler/SchedulerBackgroundService.cs`, `Server/Program.cs`

## Problema

El mensaje de interacción (texto con botones **+ la imagen**) llegaba **dos veces**
al chat de Telegram: aparecían dos copias idénticas.

## Causa raíz

Un mismo trabajo se ejecutaba dos veces. En una sola instancia y en modo polling
el flujo es secuencial y envía una sola vez, así que un duplicado **consistente**
implica que dos procesos hacen el mismo trabajo. Hay dos vías:

1. **Update de Telegram procesado dos veces** → el pipeline se reanuda por
   duplicado y reenvía la **siguiente** interacción. Ocurre por:
   - reintento del webhook cuando la respuesta `200 OK` tarda (reanudar el
     pipeline puede ser lento),
   - reproceso en polling tras un reinicio (el offset se confirma en la siguiente
     llamada a `getUpdates`),
   - **dos instancias** de la app (dos contenedores, o un rolling deploy
     solapado) consumiendo el mismo update.

2. **Schedule ejecutado dos veces** → el **primer** envío de la interacción se
   duplica. El "claim" avanzaba `NextRunAt` con un `SELECT`+`UPDATE` (load-modify-save)
   sin garantía de atomicidad, así que dos workers podían leer la misma fila
   pendiente, avanzarla ambos y ejecutar los dos.

Un guard **en memoria** no cubre 1: no cruza instancias ni sobrevive reinicios.

## Solución implementada

Idempotencia respaldada en la **base de datos compartida**, que es atómica y
válida entre procesos.

### 1. Deduplicación de updates (`TelegramUpdateHandler`)

- Nueva tabla `ProcessedTelegramUpdates(UpdateKey text PK, ProcessedAt timestamptz)`
  creada en el arranque (`Program.cs`, junto a las demás migraciones de CoreDb).
- `TelegramService.GetUpdateId(JsonElement)` extrae el `update_id`.
- `ProcessUpdateAsync` reclama el update al inicio con
  `INSERT ... ON CONFLICT (UpdateKey) DO NOTHING`; si afecta 0 filas, el update ya
  se procesó (otra entrega/instancia) y se ignora. La clave es `"{chatId}:{update_id}"`.
- Poda oportunista de filas con más de 1 día (Telegram nunca reintenta un update
  tan antiguo), así que la tabla se mantiene pequeña.

### 2. Claim atómico del scheduler (`SchedulerBackgroundService`)

- El avance de `NextRunAt` pasa a un **UPDATE condicional** único:
  `WHERE Id = @id AND NextRunAt = @previous` vía `ExecuteUpdateAsync`. Solo el
  worker que aún ve el `NextRunAt` viejo gana (1 fila); un worker concurrente
  encuentra la fila ya avanzada (0 filas) y salta el run. La ruta de reintento
  transitorio también usa `ExecuteUpdateAsync` para no depender de la entidad
  rastreada, ya desincronizada tras el claim.

## Cómo verificar el fix

- **Manual**: pulsar "Continuar" en una interacción y comprobar que la siguiente
  llega **una sola vez**; lanzar un schedule y comprobar que el primer preview
  llega **una sola vez**. Un duplicado detectado aparece en logs como
  `[TG-Update] Ignored duplicate update {chatId}:{update_id}` o
  `Schedule {Id} ... already claimed by another worker; skipping`.
- **SQL**: `SELECT count(*) FROM "ProcessedTelegramUpdates";` crece con cada
  update distinto y se poda pasado 1 día.

## Verificación de éxito

✅ Cada update se procesa una sola vez (webhook, polling y varias instancias)
✅ Cada schedule se ejecuta una sola vez aunque haya dos workers
✅ La interacción (mensaje de opciones + imagen) se envía **una** vez

## Nota operativa

La causa más habitual de un duplicado **constante** es tener **dos instancias**
de la app corriendo a la vez (p.ej. un `docker compose up` sin bajar la anterior,
o una instancia de desarrollo con el mismo bot token). Estos cambios hacen el
sistema idempotente frente a ello, pero conviene confirmar que solo hay **un**
proceso activo con `docker compose ps`.
