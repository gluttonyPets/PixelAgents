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

## 3. El analista de aprendizaje (autónomo)

Cada abort con comentario dispara —en segundo plano— un **analista**: un modelo
de IA configurable por proyecto que procesa el fallo y destila el aprendizaje.

### Configuración (por pipeline)
En **Configuración del proyecto → Aprendizaje**: un toggle para activarlo y un
desplegable de modelo de texto (filtrado por API keys, marcando cuáles son
multimodales). Se guarda en `Project`: `LearningEnabled`, `AnalystModelProvider`,
`AnalystModelName`. Endpoints: `GET/PUT /api/projects/{id}/learning-config` y
`GET /api/analyst/models`.

### Qué hace (`LearningAnalysisService`, self-contained, singleton)
1. **Atribución**: identifica el/los módulo(s) culpables (puede ser aguas arriba),
   por nombre. Se guarda en `LearningEntry.AttributionsJson`.
2. **Crítica de imagen**: si el modelo es multimodal y hubo imágenes, las analiza
   contra la definición del usuario y dice qué se generó mal y qué hacer.
3. **Destilación + reconciliación**: produce una conclusión y actualiza el
   documento vivo evitando duplicados y resolviendo contradicciones
   (`docAction`: added/reinforced/updated/skipped_duplicate/resolved_contradiction).

Todo es **autónomo**: no requiere aprobación humana. Nunca toca los prompts del
usuario ni las Reglas globales (que siguen siendo humanas).

### El documento vivo y su histórico
- `ProjectLearningDoc` (uno por proyecto): `Content` markdown descargable
  (`GET /api/projects/{id}/learning/download`) + `ActiveLearningsJson` (aprendizajes
  activos etiquetados por módulo, lo que se inyecta).
- `LearningEntry` (append-only): una fila por análisis, con atribución, conclusión,
  crítica de imagen, acción sobre el documento y errores. Visible en la web.

### Cómo se cierra el bucle (capa de inyección)
Los aprendizajes activos se inyectan como una **capa aparte y etiquetada**
(`AiExecutionContext.PastExecutionsLearning`), con cabecera
`=== APRENDIZAJE DE EJECUCIONES PASADAS ===`, filtrada por módulo
(`LearningInjection`). Nunca modifica el `systemPrompt` del usuario: es un bloque
adicional. Se serializa en `StepPayloadBuilder` (`pastExecutionsLearning`) y en la
web se muestra **diferenciada** en el detalle de ejecución. Se mantiene acotada por
consolidación del analista (no se acumulan ejemplos sin fin).

## 4. Roadmap posterior

- Feedback **positivo** (ejemplos de lo que sí gustó), hoy solo negativo.
- Recuperación por relevancia (embeddings) si los aprendizajes por módulo crecen.
- Atribución con botones de módulo en Telegram para afinar la culpa manualmente.
