# Fix: Timeout y cancelación del pipeline

> Estado: **resuelto** · Fecha: 2026-05-30 · Área: providers de IA + `GraphPipelineExecutor`

## Problema

El pipeline se quedaba bloqueado cuando un módulo no recibía respuesta de su
proveedor de IA:

- No había timeout para liberar la ejecución.
- El botón "Cancelar" no detenía realmente el trabajo en curso.
- Como workaround había que duplicar el pipeline para seguir trabajando.

La infraestructura de cancelación ya existía (`ExecutionCancellationService`,
endpoint `/api/projects/{projectId}/cancel`, botón en la UI), pero los providers
no respetaban el `CancellationToken`, así que las llamadas HTTP en vuelo nunca se
interrumpían.

## Solución

Se aplicaron **dos niveles de protección** complementarios.

### 1. Timeout + cancelación a nivel de provider

Se añadió un `CancellationToken` a `AiExecutionContext` (`Server/Models/AiExecution.cs`)
y los module handlers lo propagan al contexto. Cada provider combina ese token
del usuario con un timeout propio mediante `CancellationTokenSource.CreateLinkedTokenSource`:

```csharp
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
    context.CancellationToken, timeoutCts.Token);
var ct = linkedCts.Token;

var completion = await client.CompleteChatAsync(messages, options, ct);
```

El token se pasa a **todas** las operaciones async (chat completion, generación
de imágenes, `HttpClient.SendAsync`/`PostAsync`, `ReadAsStringAsync`,
`Task.Delay` en bucles de polling, etc.), de modo que cancelar detiene las
llamadas en progreso de inmediato.

Timeouts por provider:

| Provider          | Timeout | Notas                                         |
|-------------------|---------|-----------------------------------------------|
| `OpenAiProvider`  | 5 min   | Text e Image                                  |
| `GeminiProvider`  | 3 min   | Text e Image; ya tenía token propagado        |
| `AnthropicProvider` | 3–5 min | 3 min vía SDK, 5 min con adjuntos vía HttpClient |

`GrokProvider` (xAI) y `LeonardoProvider` no fijan un timeout propio, pero sí
propagan el `CancellationToken` a todas sus llamadas async (chat completion,
`PostAsync`/`GetAsync`, `ReadAsStringAsync`, descargas de imagen y el
`Task.Delay` del bucle de polling de Leonardo), de modo que un "Cancelar"
manual corta sus llamadas al instante. El tope duro lo sigue poniendo el
timeout de módulo del executor (ver abajo).

### 2. Timeout a nivel de módulo (`GraphPipelineExecutor.cs`)

Red de seguridad que protege **cualquier** handler, tenga o no timeout interno:
10 minutos por módulo.

```csharp
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
ctx.CancellationToken = linkedCts.Token;

try
{
    var handlerTask = handler.ExecuteAsync(ctx);
    var completedTask = await Task.WhenAny(
        handlerTask, Task.Delay(Timeout.Infinite, linkedCts.Token));

    if (completedTask != handlerTask)
    {
        if (ct.IsCancellationRequested)
            return ModuleResult.Failed("Ejecución cancelada por el usuario");
        return ModuleResult.Failed("El módulo excedió el tiempo límite de 10 minutos");
    }
    return await handlerTask;
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    return ModuleResult.Failed("Ejecución cancelada por el usuario");
}
catch (OperationCanceledException)
{
    return ModuleResult.Failed("El módulo excedió el tiempo límite de 10 minutos");
}
```

### ¿Por qué dos niveles?

- **Timeout de provider (≈5 min)**: detecta antes problemas de red o de API.
- **Timeout de módulo (10 min)**: red de seguridad para providers sin timeout
  interno o para operaciones legítimamente largas.

### 3. Cancelación de ejecuciones programadas (`SchedulerBackgroundService.cs`)

Las ejecuciones lanzadas por el planificador (cron/cola de prompts) corrían con
el `stoppingToken` de la app, que **no** está en `ExecutionCancellationService`.
Resultado: el botón "Cancelar" llamaba a `Cancel(projectId)`, no encontraba
ningún token registrado y no hacía nada — la ejecución automática era imposible
de parar desde la UI.

Ahora el scheduler registra cada run igual que el endpoint manual y enlaza el
token de usuario con el de apagado de la app:

```csharp
var userToken = _cancellation.Register(schedule.ProjectId);
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(userToken, ct);
try
{
    await executor.ExecuteAsync(schedule.ProjectId, effectiveInput, execDb, dbName,
        linkedCts.Token, schedule.UseHistory);
}
finally
{
    _cancellation.Remove(schedule.ProjectId);
}
```

Al cancelar, la ejecución que quedaba en estado `Running` se marca como
`Cancelled` para que no aparezca como un job fantasma en el historial.

## Flujo de cancelación completo

1. El usuario pulsa "Cancelar" en la UI → `Api.CancelExecutionAsync(ProjectId)`.
2. `POST /api/projects/{projectId}/cancel`.
3. `ExecutionCancellationService.Cancel(projectId)` cancela el token (ya sea de
   una ejecución manual **o programada**).
4. El token se propaga hasta las operaciones async del provider.
5. Las llamadas HTTP y `Task.Delay` se interrumpen → `OperationCanceledException`.
6. `GraphPipelineExecutor.RunGraphAsync` es el **único punto** por el que pasan
   todas las rutas (Execute, Retry, Resume\*). Ahí se captura la cancelación y se
   marca la ejecución como `Cancelled` (con `CancellationToken.None`, porque el
   token ambiente ya está cancelado), cerrando también los steps que quedaban en
   `Running`. Así la fila nunca se queda como "En curso" en el historial.
7. El servidor avisa al cliente:
   - Manual: el endpoint envía `ExecutionCancelled` por SignalR (sin banner de error).
   - Programada: no hay SignalR, pero el *polling* del cliente detecta el estado
     terminal y refresca vista e historial.

### Persistencia del estado y refresco de la UI

Antes, al cancelar, el executor lanzaba la excepción **antes** de llegar a
`FinalizeGraphAsync`, así que la ejecución se quedaba en `Running` en la BD y
dependía de que cada llamador la arreglara en su `catch`. Ahora el estado
`Cancelled` se persiste de forma central en `RunGraphAsync`, y el frontend
(`ProjectDetail.razor`) recarga ejecuciones tanto en `ExecutionCancelled` como
en `ExecutionFailed`, y mantiene el *polling* armado tras pulsar "Cancelar".

## Configuración de timeouts

```csharp
// Por provider (p. ej. OpenAiProvider.cs)
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(X));

// Global por módulo (GraphPipelineExecutor.cs)
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(X));
```

## Notas técnicas

- Los `CancellationTokenSource` van en `using`: se liberan solos y cierran las
  conexiones HTTP al cancelar.
- El impacto en rendimiento es negligible (<1 ms por verificación).
- Patrón extensible: cualquier provider nuevo puede sumar su propio timeout
  siguiendo el mismo `CreateLinkedTokenSource`.

## Archivos modificados

```
Server/Models/AiExecution.cs                        (CancellationToken en el contexto)
Server/Services/Ai/OpenAiProvider.cs                (timeout 5 min + token)
Server/Services/Ai/GeminiProvider.cs                (timeout 3 min + token)
Server/Services/Ai/AnthropicProvider.cs             (timeout 3–5 min + token)
Server/Services/Ai/GrokProvider.cs                  (propaga el token en chat/imagen)
Server/Services/Ai/LeonardoProvider.cs              (propaga el token en submit/polling/descarga)
Server/Services/Ai/GraphPipelineExecutor.cs         (timeout 10 min + estado Cancelled central)
Server/Services/Ai/Handlers/*.cs                    (propagan el token al contexto)
Server/Services/Scheduler/SchedulerBackgroundService.cs  (registra el token en runs programados)
Server/Program.cs                                   (endpoint /execute avisa ExecutionCancelled)
Client/Pages/ProjectDetail.razor                    (refresca UI/historial al cancelar)
```
