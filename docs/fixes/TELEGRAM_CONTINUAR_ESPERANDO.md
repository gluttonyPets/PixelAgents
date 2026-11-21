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

## Verificación

`Server.Tests/HilosTelegram/ContinuarEnSegundoPlanoTests.cs`:

- el update se procesa sin ejecutar el pipeline y la correlación queda cerrada; al correr el
  trabajo, reanuda esa ejecución y envía la siguiente interacción en cola;
- un fallo antes de arrancar reabre la pregunta y reenvía los botones;
- un fallo a mitad del pipeline no la reabre.

Sin dispatcher inyectado, el handler ejecuta el trabajo en línea (lo usan los tests existentes).
