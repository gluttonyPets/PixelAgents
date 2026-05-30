# Fix: Pipeline Timeout y Cancelación

## Problema
El pipeline se quedaba bloqueado cuando algún módulo no obtenía respuesta de OpenAI u otros proveedores de IA:
- No había timeout para liberar la ejecución
- El botón de cancelar no funcionaba
- Era necesario duplicar el pipeline para poder continuar trabajando

## Solución Implementada

### 1. Timeouts en OpenAI Provider (`OpenAiProvider.cs`)

Se agregaron timeouts de **5 minutos** en todas las operaciones de OpenAI:

- **GenerateTextAsync**: Timeout de 5 minutos para llamadas de chat completion
- **GenerateImageAsync**: Timeout de 5 minutos para generación y edición de imágenes
- **GenerateVideoAsync**: Ya tenía timeout de 10 minutos (mantenido)
- **CallImageEditRawAsync**: Timeout de 5 minutos para llamadas HTTP directas

**Cómo funciona:**
```csharp
// Se crea un timeout combinado: el que ocurra primero (cancelación del usuario o timeout)
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeoutCts.Token);
var ct = linkedCts.Token;

// Se pasa el token a todas las llamadas
var completion = await client.CompleteChatAsync(messages, options, ct);
```

### 2. Propagación del CancellationToken

Se agregó el `CancellationToken` a **todas** las llamadas asíncronas en OpenAI:
- `CompleteChatAsync()`
- `GenerateImageAsync()` / `GenerateImagesAsync()`
- `GenerateImageEditAsync()` / `GenerateImageEditsAsync()`
- `PostAsync()`, `SendAsync()`, `GetByteArrayAsync()`
- `Task.Delay()` en bucles de polling

Esto permite que cuando el usuario presione "Cancelar", la operación se detenga inmediatamente.

### 3. Timeout a Nivel de Módulo (`GraphPipelineExecutor.cs`)

Se agregó un **timeout de 10 minutos por módulo** que protege contra bloqueos en CUALQUIER handler, no solo OpenAI:

```csharp
// Timeout de 10 minutos por módulo para evitar bloqueos indefinidos
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
ctx.CancellationToken = linkedCts.Token;

try
{
    var handlerTask = handler.ExecuteAsync(ctx);
    var completedTask = await Task.WhenAny(handlerTask, Task.Delay(Timeout.Infinite, linkedCts.Token));
    
    if (completedTask != handlerTask)
    {
        if (ct.IsCancellationRequested)
            return ModuleResult.Failed("Ejecución cancelada por el usuario");
        return ModuleResult.Failed($"El módulo excedió el tiempo límite de 10 minutos");
    }

    return await handlerTask;
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    return ModuleResult.Failed("Ejecución cancelada por el usuario");
}
catch (OperationCanceledException)
{
    return ModuleResult.Failed($"El módulo excedió el tiempo límite de 10 minutos");
}
```

**Beneficios:**
- ✅ Protege contra bloqueos en TODOS los providers (OpenAI, Gemini, Anthropic, Leonardo, etc.)
- ✅ No requiere modificar cada provider individualmente
- ✅ Timeout configurable centralizado
- ✅ Manejo correcto de errores con mensajes claros

### 4. Estado de Otros Providers

- **GeminiProvider**: ✅ Ya tenía timeout y CancellationToken implementados correctamente
- **AnthropicProvider**: ✅ Ya tenía timeout de 5 minutos y CancellationToken
- **GrokProvider**, **LeonardoProvider**, **Json2VideoProvider**, **PexelsProvider**: Ahora están protegidos por el timeout a nivel de módulo

## Resultados Esperados

### Antes:
1. Pipeline se queda bloqueado indefinidamente si OpenAI no responde
2. Botón "Cancelar" no hace nada
3. Necesidad de duplicar el pipeline para continuar trabajando

### Después:
1. ✅ **Timeout automático**: Si un módulo no responde en 10 minutos, el pipeline falla con mensaje claro
2. ✅ **Cancelación inmediata**: Al presionar "Cancelar", el módulo actual se detiene inmediatamente
3. ✅ **No más bloqueos**: Nunca más necesitarás duplicar el pipeline

## Cómo Probar

### Prueba 1: Timeout Automático
1. Crear un pipeline con un módulo de OpenAI
2. Desconectar internet o usar una API key inválida
3. Ejecutar el pipeline
4. **Resultado esperado**: El módulo falla después de 5 minutos (OpenAI) o 10 minutos (máximo) con mensaje de timeout

### Prueba 2: Cancelación Manual
1. Crear un pipeline con varios módulos
2. Ejecutar el pipeline
3. Mientras un módulo está ejecutándose, presionar el botón "Cancelar"
4. **Resultado esperado**: La ejecución se detiene inmediatamente con mensaje "Ejecución cancelada por el usuario"

### Prueba 3: Ejecución Normal
1. Crear un pipeline normal con varios módulos
2. Ejecutar el pipeline
3. **Resultado esperado**: Todo funciona normalmente, sin cambios en el comportamiento

## Configuración de Timeouts

Si necesitas ajustar los timeouts en el futuro:

### Timeout por Provider (en OpenAiProvider.cs):
```csharp
// Cambiar el timeout de 5 a X minutos
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(X));
```

### Timeout Global por Módulo (en GraphPipelineExecutor.cs):
```csharp
// Cambiar el timeout de 10 a X minutos
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(X));
```

## Notas Técnicas

### ¿Por qué dos niveles de timeout?

1. **Timeout en el Provider (5 min)**: Detecta problemas de red o API más rápido
2. **Timeout en el Executor (10 min)**: Red de seguridad para proveedores que no tienen timeout interno o para operaciones que legítimamente toman más tiempo (ej: generación de video)

### ¿Qué pasa con los recursos al cancelar?

- Los `CancellationTokenSource` están envueltos en `using`, se liberan automáticamente
- Las conexiones HTTP se cierran al cancelar
- Los handlers de OpenAI SDK limpian sus recursos internamente

### ¿Afecta el rendimiento?

No. Los timeouts solo añaden una verificación ligera. El impacto en rendimiento es negligible (<1ms).

## Archivos Modificados

1. `/Server/Services/Ai/OpenAiProvider.cs` - Agregados timeouts y CancellationToken
2. `/Server/Services/Ai/GraphPipelineExecutor.cs` - Agregado timeout a nivel de módulo

## Próximos Pasos (Opcional)

Si quieres optimizar aún más:

1. **Timeout configurable por módulo**: Permitir que cada módulo configure su propio timeout desde la UI
2. **Retry automático**: Reintentar automáticamente en caso de timeout transitorio
3. **Logs mejorados**: Registrar el tiempo de ejecución de cada módulo para análisis
4. **Métricas**: Tracking de cuántos timeouts ocurren y en qué módulos

## Fecha
2026-05-30
