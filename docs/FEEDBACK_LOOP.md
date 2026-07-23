# Bucle de aprendizaje: feedback de ejecuciones

Mecanismo para capturar qué ejecuciones no gustan al usuario y usarlo, con el
tiempo, para que los pipelines sean más precisos. Este documento cubre la
**primera fase ya implementada** (captura por Telegram + visualización en web) y
el **roadmap** de las siguientes.

## 1. Qué hace hoy

### Captura por Telegram (al abortar)
Cuando el usuario pulsa **Abortar** en una interacción de Telegram:

1. El pipeline se aborta como siempre (`AbortFromInteractionAsync` +
   `CancelQueuedInteractionsAsync`).
2. La correlación **no se cierra**: pasa al estado `awaiting_abort_feedback` y el
   bot pregunta *"¿Qué ha ido mal?"*.
3. La siguiente respuesta de texto se guarda como `ExecutionFeedback`
   (`Rating = "negative"`, `Source = "telegram_abort"`), asociada a la ejecución
   y al módulo pausado. Enviar `-` omite el comentario.

El estado vive en `TelegramCorrelation.State` y se maneja en
`TelegramUpdateHandler`. Como la ejecución ya no está en `WaitingForInput`, el
estado se añade a la lista de estados "fuera de banda" de
`FindValidCorrelationAsync` para que la correlación no se descarte.

### Visualización en web
En **Detalle de proyecto → Historial**, cada ejecución con feedback muestra un
badge `💬 Feedback`; al expandir la tarjeta se ve el comentario, su origen y la
fecha. Los datos se cargan desde `GET /api/projects/{projectId}/feedback`
(también existe `GET /api/executions/{executionId}/feedback`).

## 2. Modelo de datos

`ExecutionFeedback` (tenant-scoped, tabla `ExecutionFeedbacks`):

| Campo             | Uso                                                        |
|-------------------|------------------------------------------------------------|
| `ExecutionId`     | ejecución valorada (FK, cascade)                           |
| `StepExecutionId` | step concreto señalado (nullable, para la fase few-shot)   |
| `ProjectModuleId` | módulo señalado (nullable, para agregar feedback por módulo)|
| `Rating`          | `negative` / `positive`                                    |
| `Comment`         | texto libre del usuario                                    |
| `Source`          | `telegram_abort`, `web`, ...                               |

La tabla se crea en tenants existentes desde `TenantDbContextFactory`
(`CREATE TABLE IF NOT EXISTS`), igual que el resto del esquema.

## 3. Roadmap (siguientes fases)

Las capas donde puede corregirse una salida son cuatro (reglas globales, memoria
de ejecuciones, prompt del módulo, input). El feedback capturado alimenta la
mejora de la capa correcta:

- **Feedback → propuesta**: a partir del comentario y del payload guardado en
  `StepExecution.InputData`, proponer si la corrección va como Regla global,
  edición del prompt del módulo (reutilizando `PromptBuilderService.AddAsync`) o
  ejemplo. Siempre con aprobación humana.
- **Few-shot / ejemplos**: guardar salidas buenas/malas como ejemplos e inyectar
  los más relevantes en el prompt del módulo. Retos abiertos: atribuir el fallo
  al módulo correcto (`ProjectModuleId`/`StepExecutionId` ya lo permiten),
  representar ejemplos de imagen (guardar el *prompt* + miniatura, no el binario),
  y evitar que el prompt crezca sin límite (seleccionar por relevancia/recencia,
  no volcar todos los ejemplos).
