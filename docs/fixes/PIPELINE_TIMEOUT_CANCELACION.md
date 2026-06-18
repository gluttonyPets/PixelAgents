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

Los providers que no fijan timeout propio (`GrokProvider`/xAI, `LeonardoProvider`)
quedan cubiertos por el timeout de módulo del executor (ver abajo).

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

## Flujo de cancelación completo

1. El usuario pulsa "Cancelar" en la UI → `Api.CancelExecutionAsync(ProjectId)`.
2. `POST /api/projects/{projectId}/cancel`.
3. `ExecutionCancellationService.Cancel(projectId)` cancela el token.
4. El token se propaga hasta las operaciones async del provider.
5. Las llamadas HTTP y `Task.Delay` se interrumpen → `OperationCanceledException`.
6. El pipeline se detiene y marca el módulo como fallido con mensaje claro.

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
Server/Models/AiExecution.cs                    (CancellationToken en el contexto)
Server/Services/Ai/OpenAiProvider.cs            (timeout 5 min + token)
Server/Services/Ai/GeminiProvider.cs            (timeout 3 min + token)
Server/Services/Ai/AnthropicProvider.cs         (timeout 3–5 min + token)
Server/Services/Ai/GraphPipelineExecutor.cs     (timeout 10 min por módulo)
Server/Services/Ai/Handlers/*.cs                (propagan el token al contexto)
```
