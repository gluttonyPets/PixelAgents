# Respuestas cruzadas entre pipelines en Telegram

> Estado: **resuelto** · Fecha: 2026-09-21 · Área: `Server/Services/Telegram`, `Server/Services/Ai/GraphPipelineExecutor.cs`

## Problema

Con dos ejecuciones esperando respuesta en el mismo chat, la respuesta a la segunda
pregunta reanudaba la primera. Pasaba tanto escribiendo texto como pulsando los botones
de un mensaje anterior: el contenido revisado de un pipeline acababa aplicado a otro.

## Causa

La atribución de un mensaje entrante se hacía sólo por chat y antigüedad:

- `FindValidCorrelationAsync` cogía las correlaciones no resueltas del chat, las ordenaba
  por `CreatedAt` y devolvía **la primera**. Cualquier mensaje caía en la más antigua.
- El `callback_data` de los botones era literal (`continue`, `abort`, …), sin ninguna
  referencia a la interacción a la que pertenecía; y nada retiraba el teclado de los
  mensajes ya resueltos, así que seguían siendo pulsables.
- La cola anticolisión (`State = "queued"`) sólo serializaba interacciones **dentro de una
  misma ejecución** (`c.ExecutionId == execution.Id`), no entre ejecuciones distintas.
- `ParseIncomingUpdate` descartaba `message_thread_id` y `reply_to_message`, los dos campos
  que Telegram ya ofrece para desambiguar.

## Solución

Correlación explícita, por orden de fiabilidad, en `ResolveCorrelationAsync`:

1. **Token en el botón.** `TelegramCorrelation.Token` (8 hex) viaja dentro del `callback_data`
   con el formato `accion|token|payload` (`TelegramCallback`, dentro del límite de 64 bytes).
   Un botón de una interacción ya cerrada se ignora y se avisa, en vez de aplicarse a otra.
2. **Mensaje citado.** Se guarda el `BotMessageId` del mensaje enviado y se compara con
   `reply_to_message.message_id`.
3. **Hilo.** Si el chat es un supergrupo con Temas, cada ejecución abre su `forum topic`
   (`createForumTopic`) y todos sus mensajes van con `message_thread_id`; lo que llega por
   ese hilo pertenece a esa ejecución. El hilo se cierra al terminar la ejecución.
4. **Única interacción abierta.** Comportamiento de siempre cuando no hay ambigüedad.

Si quedan varias abiertas y el mensaje no trae pista alguna, el texto se guarda en
`TelegramPendingReplies` y el bot pregunta con botones `pick|token` a cuál corresponde.
Además, al atender una interacción se le retira el teclado (`editMessageReplyMarkup`) y,
cuando no hay hilos, cada mensaje se etiqueta con `#TOKEN · Proyecto · Paso`.

## Verificación

`Server.Tests/HilosTelegram` (`dotnet test Server.Tests/Server.Tests.csproj`):

- un botón con el token de B reanuda B y deja A intacta;
- una respuesta citando el mensaje de B va a B;
- un mensaje enviado en el hilo de B va a B;
- un texto ambiguo no reanuda nada, se guarda como pendiente y se aplica a la interacción
  que el usuario elige;
- un botón de una interacción ya cerrada no toca la otra;
- con una sola interacción abierta, el texto suelto sigue funcionando como antes.

Las correlaciones anteriores a este cambio no tienen token: siguen resolviéndose por el
camino clásico y se les asigna uno la primera vez que hace falta desambiguar.
