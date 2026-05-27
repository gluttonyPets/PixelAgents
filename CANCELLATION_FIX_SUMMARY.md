# Resumen de Cambios: Cancelación de Pipeline y Timeouts

## Problema
1. **Pipeline se queda pillado**: Los módulos (especialmente Gemini) se quedaban atascados sin timeout
2. **Botón Cancelar no funciona**: Aunque la infraestructura de cancelación existía, los providers no respetaban el CancellationToken

## Solución Implementada

### 1. Agregado CancellationToken a AiExecutionContext
- **Archivo**: `Server/Models/AiExecution.cs`
- **Cambio**: Agregada propiedad `CancellationToken` al contexto de ejecución de IA

### 2. Actualización de Todos los Module Handlers
Se actualizaron todos los handlers para pasar el CancellationToken al AiExecutionContext:
- `ImageModuleHandler.cs`
- `TextModuleHandler.cs`
- `VideoModuleHandler.cs`
- `AudioModuleHandler.cs`
- `DesignModuleHandler.cs`
- `EmbeddingsModuleHandler.cs`
- `TranscriptionModuleHandler.cs`
- `VideoEditModuleHandler.cs`
- `VideoSearchModuleHandler.cs`
- `CoordinatorModuleHandler.cs`
- `OrchestratorModuleHandler.cs`

### 3. GeminiProvider - Timeouts y Cancelación
**Archivo**: `Server/Services/Ai/GeminiProvider.cs`

#### Cambios realizados:
1. **Timeouts configurados**:
   - Text: 3 minutos
   - Image: 3 minutos  
   - Video: 8 minutos (ya existía)

2. **Método helper `ExecuteWithTimeoutAsync<T>`**:
   - Combina timeout con CancellationToken del usuario
   - Lanza `OperationCanceledException` si el usuario cancela
   - Lanza `TimeoutException` si se excede el tiempo límite

3. **Manejo de OperationCanceledException**:
   - Captura la excepción y devuelve mensaje amigable

4. **CancellationToken en todas las operaciones async**:
   - `Task.Delay` con CancellationToken en reintentos de GenerateImageAsync
   - `HttpClient.SendAsync` con CancellationToken en GenerateVideoAsync
   - `ReadAsStringAsync` / `ReadAsByteArrayAsync` con CancellationToken
   - Wrapping de llamadas SDK con `ExecuteWithTimeoutAsync`

### 4. AnthropicProvider - Timeouts y Cancelación
**Archivo**: `Server/Services/Ai/AnthropicProvider.cs`

#### Cambios realizados:
1. **Timeout configurado**:
   - Text: 3 minutos (en SDK path)
   - Text con attachments: 5 minutos (ya existía en HttpClient)

2. **Método helper `ExecuteWithTimeoutAsync<T>`**:
   - Mismo helper que GeminiProvider

3. **Manejo de OperationCanceledException**:
   - Captura la excepción y devuelve mensaje amigable

4. **CancellationToken en operaciones HTTP**:
   - `HttpClient.PostAsync` con CancellationToken en GenerateTextWithAttachmentsAsync
   - `ReadAsStringAsync` con CancellationToken
   - Wrapping de llamadas SDK con `ExecuteWithTimeoutAsync`

## Infraestructura de Cancelación Existente (Ya Funcionaba)
- **ExecutionCancellationService**: Gestiona CancellationTokenSource por proyecto
- **Endpoint**: `/api/projects/{projectId}/cancel` para cancelar ejecuciones
- **Cliente**: Botón "Cancelar" llama al endpoint correctamente
- **GraphPipelineExecutor**: Ya respetaba el CancellationToken en el bucle principal

## Flujo de Cancelación Completo

1. **Usuario presiona "Cancelar"** en la UI
2. Cliente llama a `Api.CancelExecutionAsync(ProjectId)`
3. POST a `/api/projects/{projectId}/cancel`
4. `ExecutionCancellationService.Cancel(projectId)` cancela el token
5. **NUEVO**: El token se propaga hasta las operaciones async del provider
6. **NUEVO**: Las llamadas HTTP y Task.Delay respetan el token
7. **NUEVO**: Se lanza OperationCanceledException
8. **NUEVO**: El pipeline se detiene y marca el módulo como fallido

## Flujo de Timeout

1. Provider inicia operación async (ej: GenerateContentAsync)
2. **NUEVO**: Wrapping con `ExecuteWithTimeoutAsync(task, timeout, ct)`
3. Task.WhenAny espera: operación O timeout O cancelación
4. **NUEVO**: Si timeout expira primero → TimeoutException
5. **NUEVO**: Si cancelación ocurre primero → OperationCanceledException
6. El pipeline captura la excepción y marca el módulo como fallido

## Beneficios

### Cancelación Real
- ✅ El botón Cancelar ahora **detiene realmente** la ejecución
- ✅ Se interrumpen las llamadas a APIs de IA en progreso
- ✅ No se queda esperando indefinidamente

### Timeouts Automáticos
- ✅ Si Gemini/Anthropic no responde en 3 minutos → timeout automático
- ✅ Si Veo (video) no responde en 8 minutos → timeout automático
- ✅ Evita que el pipeline se quede "pillado" indefinidamente

### Mejor UX
- ✅ El usuario recibe feedback inmediato al cancelar
- ✅ Mensajes de error claros ("Operación cancelada por el usuario")
- ✅ El pipeline no se queda bloqueado consumiendo recursos

## Testing Recomendado

1. **Test de Cancelación**:
   - Iniciar pipeline con módulo Gemini (Text o Image)
   - Presionar "Cancelar" mientras está en ejecución
   - Verificar que se detiene inmediatamente
   - Verificar mensaje de error apropiado en logs

2. **Test de Timeout**:
   - Configurar red lenta o mock que tarde más de 3 minutos
   - Verificar que se lanza TimeoutException
   - Verificar mensaje de error con tiempo límite

3. **Test de Ejecución Normal**:
   - Ejecutar pipeline completo sin cancelar
   - Verificar que todo funciona como antes
   - Verificar que no hay regresiones

## Archivos Modificados

```
Server/Models/AiExecution.cs
Server/Services/Ai/GeminiProvider.cs
Server/Services/Ai/AnthropicProvider.cs
Server/Services/Ai/Handlers/ImageModuleHandler.cs
Server/Services/Ai/Handlers/TextModuleHandler.cs
Server/Services/Ai/Handlers/VideoModuleHandler.cs
Server/Services/Ai/Handlers/AudioModuleHandler.cs
Server/Services/Ai/Handlers/DesignModuleHandler.cs
Server/Services/Ai/Handlers/EmbeddingsModuleHandler.cs
Server/Services/Ai/Handlers/TranscriptionModuleHandler.cs
Server/Services/Ai/Handlers/VideoEditModuleHandler.cs
Server/Services/Ai/Handlers/VideoSearchModuleHandler.cs
Server/Services/Ai/Handlers/CoordinatorModuleHandler.cs
Server/Services/Ai/Handlers/OrchestratorModuleHandler.cs
```

## Notas Técnicas

- El helper `ExecuteWithTimeoutAsync` usa `Task.WhenAny` para race entre task, timeout y cancelación
- Los CancellationTokenSource se enlazan (`CreateLinkedTokenSource`) para combinar timeout + cancelación del usuario
- Los timeouts son configurables mediante constantes en cada provider
- La solución es extensible a otros providers (OpenAI, etc) siguiendo el mismo patrón
