# "Continuar" en Telegram y la ejecución sigue en "Esperando"

> Estado: **resuelto** · Fecha: 2026-11-21 · Área: `Server/Services/Telegram`

## Problema

Se pulsaba "Continuar" en Telegram y la ejecución seguía en `WaitingForInput` ("Esperando"):
a veces durante mucho rato, y a veces para siempre, hasta que la rescataba la pasada diaria
de `StaleInteractionResumeService`.

## Causa

1. **El pipeline reanudado corría dentro del procesado del update.** `TelegramUpdateHandler`
   hacía `await ResumeFromInteractionAsync(...)`, que ejecuta todo lo que queda del pipeline
   (imagen, vídeo, voz, publicación…). En modo polling (producción, con `BaseUrl` http)
   `TelegramPollingService` procesa los updates uno a uno y espera a todos los bots, así que
   mientras ese pipeline corría **ningún otro botón de ningún pipeline se atendía**. El
   "Continuar" de otra ejecución quedaba en cola y el botón caducaba. "Reiniciar" y
   "Siguiente ejecución" (`ExecuteAsync`) tenían el mismo problema.
2. **Un fallo dejaba la ejecución huérfana.** Si la reanudación lanzaba una excepción antes de
   arrancar, el `catch` marcaba la correlación como resuelta: la ejecución seguía esperando
   pero ya no había pregunta abierta ni botones que pulsar.

## Solución

- `ITelegramWorkDispatcher` (`TelegramWorkDispatcher.cs`): lo que ejecuta un pipeline desde
  Telegram se lanza con `Task.Run` en un scope propio (`BackgroundTelegramWorkDispatcher`,
  singleton). El update se contesta al momento.
- Al continuar, la correlación se cierra **antes** de reanudar. Si el pipeline llega a otra
  Interacción, se envía al momento; al acabar se envía la siguiente en cola, como antes. Un
  doble clic ya no reanuda dos veces: el segundo ve la interacción cerrada.
- Si la reanudación falla y la ejecución sigue en `WaitingForInput`, la correlación se
  reabre (`waiting`) y se reenvían los botones con el error. Si falló a mitad (la ejecución ya
  no espera), solo se avisa del error.
- `StaleInteractionResumeService` se mantiene como red para reinicios del servidor.

### Segunda parte: el clic no llegaba a su pregunta (2026-11-22)

Con lo anterior desplegado seguía pasando: el log de ejecución acababa en el `step-start` de
la Interacción y la pasada de las 09:00 no la rescataba, señal de que la correlación seguía
abierta, es decir, el clic nunca se emparejó. Vías cerradas:

- **Chat guardado distinto del que manda Telegram.** La correlación guardaba el `ChatId` de
  la conexión tal cual (puede ser `@canal`), y el clic llega con el id numérico. Ahora la
  correlación guarda el id numérico (`TelegramService.ResolveNumericChatIdAsync`, con
  `getChat` y caché) y un botón con token se busca por token en cualquier chat (las
  correlaciones antiguas se corrigen al primer clic). El aviso de "interacción cerrada"
  también busca por token.
- **Webhook activo en modo polling.** Con un webhook registrado, `getUpdates` responde 409 y
  no entrega nada. `TelegramPollingService` lo elimina al ver ese 409, y
  `EnsureTelegramWebhookAsync` ya no intenta registrar uno con una URL http (Telegram exige
  HTTPS), sino que lo borra.
- **Offsets del polling.** Cada bot se sondea en paralelo y todos escribían en un
  `Dictionary` normal; ahora es `ConcurrentDictionary`.
- **Trazas en el log de ejecución.** `[Telegram] Pregunta #TOKEN enviada…` (o "en cola") y
  `[Telegram] Respuesta recibida…`: si un log acaba tras "enviada" sin "recibida", el clic
  no llegó al servidor o no casó; mirar `[TG-Update]` / `[TG-Polling]` en el log del
  contenedor.

## Verificación

`Server.Tests/HilosTelegram/ContinuarEnSegundoPlanoTests.cs`:

- el update se procesa sin ejecutar el pipeline y la correlación queda cerrada; al correr el
  trabajo, reanuda esa ejecución y envía la siguiente interacción en cola;
- un fallo antes de arrancar reabre la pregunta y reenvía los botones;
- un fallo a mitad del pipeline no la reabre.

`Server.Tests/HilosTelegram/ClicConChatDistintoTests.cs`: un botón con token reanuda aunque la
correlación guarde `@canal` y se queda con el id numérico; un id numérico no llama a Telegram
y un `@canal` se resuelve con `getChat`.

Sin dispatcher inyectado, el handler ejecuta el trabajo en línea (lo usan los tests existentes).
