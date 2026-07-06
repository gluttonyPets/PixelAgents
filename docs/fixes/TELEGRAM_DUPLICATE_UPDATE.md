# Fix: publicación enviada dos veces en el chat de Telegram

> Estado: **resuelto** · Fecha: 2026-07-06 · Área: `Server/Services/Telegram/` (`TelegramUpdateHandler`, `TelegramUpdateDeduplicator`)

## Problema

Al pulsar **"Continuar"** (o responder) sobre un módulo de interacción, la
publicación se enviaba **dos veces** al chat de Telegram: aparecían dos mensajes
idénticos con el mismo contenido/imagen.

## Causa raíz

El mismo `update` de Telegram se procesaba dos veces porque no había ninguna
protección de idempotencia. Telegram reentrega el mismo `update_id` en dos
situaciones, y ambas rutas (`webhook` y `polling`) desembocan en
`TelegramUpdateHandler.ProcessUpdateAsync`:

- **Webhook** (`POST /api/webhooks/telegram`): el endpoint ejecutaba
  `ProcessUpdateAsync` completo —que **reanuda todo el pipeline**
  (`ResumeFromInteractionAsync`) y puede tardar bastante— **antes** de devolver
  `200 OK`. Si la respuesta no llega a tiempo, Telegram **reintenta el mismo
  update**. El reintento llega mientras la primera ejecución aún no ha marcado la
  correlación como resuelta, así que `FindValidCorrelationAsync` vuelve a
  encontrarla → se reanuda otra vez → la siguiente publicación se envía duplicada.

- **Polling** (`getUpdates`): un update recibido pero aún no confirmado (el
  offset se confirma en la siguiente llamada a `getUpdates`) puede volver a
  devolverse tras un reinicio del contenedor y reprocesarse.

La deduplicación por correlación (`IsResolved`) no bastaba: la correlación se
marca resuelta **al final** del `resume`, por lo que un reintento que llega
durante el procesado la ve todavía activa.

## Solución implementada

Guard de idempotencia por `update_id` que garantiza que cada update se maneja
una sola vez, independientemente del modo (webhook o polling).

- **`Server/Services/Telegram/TelegramUpdateDeduplicator.cs`** (nuevo): servicio
  singleton, thread-safe, que recuerda los `update_id` procesados recientemente.
  Las entradas caducan a los 10 min (Telegram nunca reintenta un update tan
  antiguo), de modo que el conjunto se mantiene pequeño sin crecer sin límite.
  `TryMarkProcessed(key)` devuelve `true` la primera vez (procesar) y `false`
  en repeticiones (ignorar).

- **`TelegramService.GetUpdateId(JsonElement)`** (nuevo): extrae el `update_id`
  de nivel superior del update.

- **`TelegramUpdateHandler.ProcessUpdateAsync`**: al inicio, calcula la clave
  `"{chatId}:{update_id}"` y, si ya se procesó, ignora el update y sale antes de
  hacer cualquier trabajo con efectos secundarios.

- **`Program.cs`**: registro del singleton
  `AddSingleton(new TelegramUpdateDeduplicator())`.

La clave combina `chatId` + `update_id`: el `update_id` es único por bot, y
añadir el `chatId` evita cualquier colisión improbable entre bots distintos.

## Cómo verificar el fix

- **Test unitario**: `Server.Tests/NoRepetirTemas/TelegramUpdateDeduplicatorTests.cs`
  cubre primera vez / duplicado / claves distintas / caducidad / clave vacía.

  ```bash
  dotnet test Server.Tests/Server.Tests.csproj \
    --filter FullyQualifiedName~TelegramUpdateDeduplicatorTests
  ```

- **Manual**: pulsar "Continuar" en una interacción de Telegram y comprobar que
  la publicación llega **una sola vez**. En los logs, un reintento aparece como
  `[TG-Update] Ignored duplicate update {chatId}:{update_id}`.

## Verificación de éxito

✅ Cada update se procesa una sola vez (webhook y polling)
✅ El botón "Continuar" reanuda el pipeline una única vez
✅ La publicación se envía **una** vez al chat
✅ Los reintentos de Telegram se detectan y se ignoran sin efectos secundarios

## Notas

- El deduplicador es **en memoria** y asume una única instancia de la app (misma
  suposición que el offset en memoria de `TelegramPollingService`). Cubre el caso
  real: reintentos de Telegram dentro de una ventana corta con la app en marcha.
  Si en el futuro se escala a varias réplicas, habría que respaldarlo en BD.
