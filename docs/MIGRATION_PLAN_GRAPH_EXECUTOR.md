# Plan: Refactorizar PipelineExecutor a Arquitectura Basada en Grafos

## Contexto

El `PipelineExecutor.cs` actual tiene 7500+ lineas porque gestiona ejecucion lineal con branches, input resolution fragil, y logica duplicada en 5+ contextos diferentes (main, branch, retry, checkpoint resume, interaction resume). Cada tipo de modulo tiene handling especial repetido en cada contexto. El sistema de branches (`BranchId`, `BranchFromStep`, deferred branches) causa bugs constantes y el save-graph endpoint genera intermediarios innecesarios (`InputMapping`, `sceneInputs`, `templateInputs`, `fieldInputs`, `coordinatorInputs`).

**Objetivo**: Reemplazar todo esto con un executor basado en grafos donde cada modulo es independiente, arranca cuando tiene todas sus entradas, y multiples modulos pueden ejecutarse en paralelo.

---

## Arquitectura Propuesta

### Principio Central
No hay orden lineal. No hay branches. Cada modulo tiene un estado (Pending -> Ready -> Running -> Completed/Failed/Paused). Un modulo pasa a Ready cuando TODOS sus puertos de entrada tienen datos. Los modulos Ready se ejecutan concurrentemente.

### Modulo "Start" (Nuevo)
- Tipo `"Start"`, provider `"System"`, sin puertos de entrada
- Un unico puerto de salida: `output_prompt` (Text)
- Captura el prompt del usuario al ejecutar
- Todo pipeline debe tener exactamente un modulo Start
- Reemplaza el concepto de `{"source":"user"}` en InputMapping

---

## Estructura de Archivos

### Nuevos archivos
```
Server/Services/Ai/
  GraphPipelineExecutor.cs     (~400 lineas) - Bucle principal de ejecucion
  ExecutionGraph.cs            (~200 lineas) - Construccion del grafo y gestion de estado
  ModuleNode.cs                (~100 lineas) - Estructuras de nodo/puerto/conexion
  PortDataResolver.cs          (~150 lineas) - Mapeo output -> port data
  PausedGraphState.cs          (~50 lineas)  - Serializacion para pause/resume
  
  Handlers/
    IModuleHandler.cs          (~50 lineas)  - Interfaz + ModuleExecutionContext + ModuleResult
    StartModuleHandler.cs      - Emite el prompt del usuario
    TextModuleHandler.cs       - Llama al provider AI, parsea output estructurado
    ImageModuleHandler.cs      - Genera N imagenes segun config
    VideoModuleHandler.cs      - Genera video desde prompt + imagen opcional
    VideoEditModuleHandler.cs  - Modo escena + modo template
    VideoSearchModuleHandler.cs - Busca en Pexels
    AudioModuleHandler.cs      - TTS
    TranscriptionModuleHandler.cs - STT
    OrchestratorModuleHandler.cs - Multi-output con AI
    CoordinatorModuleHandler.cs  - Agrega multiples entradas + AI
    SceneModuleHandler.cs      - Construye JSON escena desde campos
    CheckpointModuleHandler.cs - Pausa para revision
    InteractionModuleHandler.cs - Envia mensaje, pausa si espera respuesta
    StaticTextModuleHandler.cs - Emite texto fijo
    FileUploadModuleHandler.cs - Copia archivos adjuntos
    PublishModuleHandler.cs    - Publica via Buffer
    DesignModuleHandler.cs     - Crea diseno Canva
```

### Archivos a modificar
```
Server/Services/Ai/IPipelineExecutor.cs     - Actualizar interfaz
Server/Program.cs                           - Simplificar save-graph, registrar handlers
Client/Models/PipelineGraphModels.cs        - Anadir modulo Start
Client/Components/Pipeline/PipelineCanvas.razor - Anadir Start a paleta
Client/Pages/Modules.razor                  - Anadir Start a catalogo
```

### Archivos que NO cambian
```
Server/Services/Ai/IAiProvider.cs           - Interfaz providers intacta
Server/Services/Ai/*Provider.cs             - Todos los providers intactos
Server/Services/Ai/OutputSchema.cs          - Parseo de output intacto
Server/Services/Ai/PricingCatalog.cs        - Precios intactos
Server/Services/Ai/InputAdapter.cs          - Sanitizacion intacta
Server/Models/ModuleConnection.cs           - Ya representa aristas del grafo
```

### Archivo a deprecar (no borrar aun)
```
Server/Services/Ai/PipelineExecutor.cs      - Marcar [Obsolete], mantener para rollback
```

---

## Modelos de Datos Clave (Runtime)

### ModuleNode
```csharp
public enum NodeStatus { Pending, Ready, Running, Completed, Failed, Paused, Skipped }

public class ModuleNode
{
    Guid ModuleId;
    ProjectModule ProjectModule;
    NodeStatus Status = Pending;
    List<InputPort> InputPorts;
    List<OutputPort> OutputPorts;
    StepOutput? Output;
    StepExecution? StepExecution;
    decimal Cost;
}
```

### InputPort / OutputPort
```csharp
public class InputPort
{
    string PortId;           // "input_prompt", "input_tpl_background"
    string DataType;         // "text", "image", "scene"
    bool IsRequired;
    bool AllowMultiple;
    List<PortConnection> Connections;  // Desde que puertos recibe datos
    List<PortData> ReceivedData;       // Datos recibidos de modulos completados
    bool IsSatisfied => !IsRequired || ReceivedData.Count > 0 || Connections.Count == 0;
}

public class OutputPort
{
    string PortId;           // "output_text", "output_image_1"
    string DataType;
    List<PortConnection> Connections;  // A que puertos envia datos
    PortData? Data;                    // Datos producidos al completar
}
```

### PortData (unidad de datos entre puertos)
```csharp
public class PortData
{
    string DataType;              // "text", "image", "video", "scene", "file"
    string? TextContent;          // Para datos de texto
    List<OutputFile>? Files;      // Para archivos (imagenes, videos)
    StepOutput? FullOutput;       // Output completo para resoluciones complejas
    string? SourcePortId;         // Puerto de origen
}
```

### ModuleExecutionContext (lo que recibe cada handler)
```csharp
public class ModuleExecutionContext
{
    ModuleNode Node;
    ExecutionGraph Graph;
    ProjectExecution Execution;
    Project Project;
    UserDbContext Db;
    string TenantDbName, WorkspacePath;
    string? PreviousSummaryContext;
    CancellationToken CancellationToken;
    IExecutionLogger Logger;
    Dictionary<string, List<PortData>> InputsByPort;  // Pre-resuelto
}
```

---

## Algoritmo de Ejecucion (GraphPipelineExecutor.RunGraphAsync)

```
1. Encontrar nodos Ready (todos sus inputs satisfechos, status=Pending)
   - El modulo Start es inmediatamente Ready (sin inputs)

2. Mientras haya nodos Running o Ready:
   a. Para cada nodo Ready: transicion a Running, lanzar su Task
   b. await Task.WhenAny(runningTasks)
   c. Cuando un task completa:
      - Completed: propagar outputs a puertos downstream, buscar nuevos Ready
      - Failed: marcar Failed, cascada failure a todos los downstream
      - Paused: marcar Paused, continuar con nodos independientes
   d. Si no hay tasks running ni nodos Ready:
      - Si todos terminales: ejecucion completa
      - Si hay Paused: guardar estado, marcar WaitingForInput

3. Estado final:
   - Todos Completed/Skipped -> "Completed"
   - Alguno Paused -> "WaitingForInput"/"WaitingForCheckpoint"
   - Alguno Failed -> "Failed"
```

### Propagacion de Outputs (ExecutionGraph.PropagateOutputs)
Cuando un modulo completa:
1. `PortDataResolver.ResolveOutputPorts(node)` extrae PortData por cada output port
   - `output_text` -> PortData con TextContent
   - `output_image_N` -> PortData con Files[N-1]
   - `output_N` (Orchestrator) -> PortData con Items[N-1].Content
   - `output_scene` -> PortData con TextContent (JSON)
   - `output_N` (Checkpoint) -> pass-through desde input_N
2. Para cada PortConnection del output, entregar PortData al input port destino
3. Comprobar si el nodo destino ahora esta Ready

### Interacciones (cola)
- Los modulos Interaction devuelven `ModuleResult { Status = Paused }`
- Se encolan en orden de llegada
- Solo se envia una interaccion al usuario a la vez
- Cuando el usuario responde, el modulo se completa y se envia el siguiente de la cola
- Los modulos que dependan del Interaction esperan hasta que se resuelva

---

## Simplificacion del Save-Graph Endpoint

### Eliminar (lineas ~889-1178 de Program.cs):
- Computacion de `InputMapping` (ya no es necesario, el grafo tiene las conexiones)
- Computacion de `sceneInputs`, `templateInputs`, `fieldInputs`, `coordinatorInputs`
- Computacion de `BranchId` / `BranchFromStep`

### Mantener:
- Guardar posiciones (PosX, PosY)
- Reemplazar conexiones en DB (ModuleConnection)
- Topological sort para StepOrder (solo para display, no ejecucion)
- Guardar SceneCounts y ModuleConfigs

---

## Cambios en Modelos de BD

### ProjectModule - campos a deprecar (no borrar aun):
| Campo | Estado | Reemplazo |
|-------|--------|-----------|
| StepOrder | Mantener | Solo para display, auto-computado por topological sort |
| BranchId | Deprecar | No usado por nuevo executor |
| BranchFromStep | Deprecar | No usado por nuevo executor |
| InputMapping | Deprecar | Reemplazado por conexiones de grafo |

### ProjectExecution - cambios:
- Anadir `PausedAtModuleId` (Guid?) junto a `PausedAtStepOrder`
- `PausedStepData` cambia formato a `PausedGraphState`
- `PausedBranches` eventualmente se elimina

---

## IPipelineExecutor - Nueva Interfaz

```csharp
public interface IPipelineExecutor
{
    Task<ProjectExecution> ExecuteAsync(Guid projectId, string? userInput, 
        UserDbContext db, string tenantDbName, CancellationToken ct = default, 
        bool useHistory = true);
    
    // Nuevo: retry por moduleId (no stepOrder)
    Task<ProjectExecution> RetryFromModuleAsync(Guid executionId, Guid moduleId, 
        string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default);
    
    // Simplificado: no hay branch interaction separada
    Task<ProjectExecution> ResumeFromInteractionAsync(Guid executionId, 
        string responseText, UserDbContext db, string tenantDbName, CancellationToken ct = default);
    
    Task<ProjectExecution> ResumeFromCheckpointAsync(Guid executionId, bool approved, 
        UserDbContext db, string tenantDbName, CancellationToken ct = default);
    
    Task<ProjectExecution> AbortFromInteractionAsync(Guid executionId, 
        UserDbContext db, string tenantDbName);
    
    // Mantener para cola de Telegram/WhatsApp
    Task SendNextQueuedInteractionAsync(Guid executionId, string chatId);
    Task CancelQueuedInteractionsAsync(Guid executionId);
    
    // Backward compat (redirige a RetryFromModuleAsync buscando por StepOrder)
    Task<ProjectExecution> RetryFromStepAsync(Guid executionId, int fromStepOrder, 
        string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default);
}
```

---

## Fases de Implementacion

### Fase 1: Infraestructura base (~2-3 dias)
1. Crear `ModuleNode.cs`, `ExecutionGraph.cs`, `PortDataResolver.cs`, `PausedGraphState.cs`
2. Crear `IModuleHandler.cs` con interfaz, contexto y resultado
3. Crear `StartModuleHandler.cs` (mas simple, valida la arquitectura)
4. Crear `StaticTextModuleHandler.cs`, `FileUploadModuleHandler.cs` (sin AI)
5. Anadir modulo Start a ModulePortRegistry, catalogo y paleta
6. Tests unitarios del grafo (build, ready detection, propagation)

### Fase 2: Module handlers (~3-4 dias)
1. Extraer cada `Handle*StepAsync` del PipelineExecutor actual a su handler
2. Prioridad: TextModuleHandler -> ImageModuleHandler -> SceneModuleHandler -> OrchestratorModuleHandler -> VideoEditModuleHandler -> resto
3. Cada handler es autocontenido: recibe InputsByPort, devuelve ModuleResult
4. NO cambiar logica interna de cada handler, solo adaptar entrada/salida

### Fase 3: GraphPipelineExecutor (~2 dias)
1. Implementar `GraphPipelineExecutor.cs` con el bucle principal
2. Registrar handlers via DI
3. Implementar RunGraphAsync con Task.WhenAny
4. Implementar pause/resume con PausedGraphState
5. Implementar retry desde modulo

### Fase 4: Integracion y simplificacion (~1-2 dias)
1. Registrar GraphPipelineExecutor como IPipelineExecutor
2. Simplificar save-graph endpoint (eliminar InputMapping, branches, etc.)
3. Actualizar endpoints de ejecucion
4. Marcar PipelineExecutor.cs como [Obsolete]

### Fase 5: Limpieza (~1 dia)
1. Deprecar campos BranchId, BranchFromStep, InputMapping en ProjectModule
2. Limpiar codigo de branches en PipelineCanvas si queda alguno
3. Verificar que todo funciona end-to-end

---

## Verificacion

### Tests funcionales
1. Pipeline lineal: Start -> Text -> Image (secuencial)
2. Pipeline paralelo: Start -> [Text + Image] (concurrente)
3. Pipeline con Orchestrator: Start -> Orchestrator -> [Scene1 + Scene2] -> VideoEdit
4. Pipeline con Interaction: Start -> Text -> Interaction -> Text (pausa/resume)
5. Pipeline con Checkpoint: Start -> Text -> Checkpoint -> Image
6. Retry desde un modulo intermedio
7. Pipeline con error en un modulo (cascada failure)
8. Pipeline con modulos desconectados (deben quedarse en Pending)

### Metricas de exito
- PipelineExecutor.cs: de 7500 lineas a 0 (deprecado)
- GraphPipelineExecutor.cs: ~400 lineas
- Total nuevo codigo: ~2200 lineas en 25 archivos
- Cada handler: 30-200 lineas, autocontenido
- Save-graph: de ~400 lineas de logica a ~80 lineas
- Cero duplicacion de logica entre contextos de ejecucion

---

## Archivos Criticos a Modificar

| Archivo | Lineas actuales | Cambio |
|---------|----------------|--------|
| `Server/Services/Ai/PipelineExecutor.cs` | 7564 | Marcar [Obsolete], extraer handlers |
| `Server/Services/Ai/IPipelineExecutor.cs` | 18 | Actualizar interfaz |
| `Server/Program.cs` | ~400 lineas de save-graph | Simplificar eliminando pasos 4-6 |
| `Client/Models/PipelineGraphModels.cs` | 340 | Anadir "Start" module type |
| `Client/Components/Pipeline/PipelineCanvas.razor` | 1900+ | Anadir Start a paleta |
| `Client/Pages/Modules.razor` | 2270+ | Anadir Start a catalogo |

## Nota sobre Concurrencia y DB

El `UserDbContext` no es thread-safe. El bucle principal usa `Task.WhenAny` para procesar completions uno a uno (single-threaded orchestration). Los handlers NO escriben a DB directamente: devuelven `ModuleResult` y el bucle principal hace todas las escrituras. Los handlers SI pueden llamar a APIs externas (providers) en paralelo ya que eso es I/O bound y no toca DB.
