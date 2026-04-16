# Progreso de Migracion: Graph Pipeline Executor

## Estado Actual: Fases 1-2 completadas. Fases 3-5 pendientes.

---

## Fase 1: Infraestructura base - COMPLETADA

### Archivos creados:
| Archivo | Lineas | Estado |
|---------|--------|--------|
| `Server/Services/Ai/ModuleNode.cs` | 83 | Completado |
| `Server/Services/Ai/ExecutionGraph.cs` | 227 | Completado |
| `Server/Services/Ai/PortDataResolver.cs` | 147 | Completado |
| `Server/Services/Ai/PausedGraphState.cs` | 79 | Completado |
| `Server/Services/Ai/Handlers/IModuleHandler.cs` | 119 | Completado |

---

## Fase 2: Module Handlers - COMPLETADA

### 18 handlers creados en `Server/Services/Ai/Handlers/`:
| Handler | Tipo | AI? | Lineas |
|---------|------|-----|--------|
| `StartModuleHandler.cs` | Start | No | 24 |
| `StaticTextModuleHandler.cs` | StaticText | No | 20 |
| `FileUploadModuleHandler.cs` | FileUpload | No | 49 |
| `SceneModuleHandler.cs` | Scene | No | 74 |
| `TextModuleHandler.cs` | Text | Si | 61 |
| `ImageModuleHandler.cs` | Image | Si | 114 |
| `VideoModuleHandler.cs` | Video | Si | 76 |
| `VideoEditModuleHandler.cs` | VideoEdit | Si | 172 |
| `VideoSearchModuleHandler.cs` | VideoSearch | Si | 64 |
| `AudioModuleHandler.cs` | Audio | Si | 64 |
| `TranscriptionModuleHandler.cs` | Transcription | Si | 56 |
| `EmbeddingsModuleHandler.cs` | Embeddings | Si | 49 |
| `OrchestratorModuleHandler.cs` | Orchestrator | Si | 111 |
| `CoordinatorModuleHandler.cs` | Coordinator | Si | 69 |
| `CheckpointModuleHandler.cs` | Checkpoint | No | 39 |
| `InteractionModuleHandler.cs` | Interaction | No | 32 |
| `DesignModuleHandler.cs` | Design | Si | 77 |
| `PublishModuleHandler.cs` | Publish | No | 24 |

---

## Fase 3: GraphPipelineExecutor - PENDIENTE

### Archivo a crear:
| Archivo | Descripcion | Estado |
|---------|-------------|--------|
| `Server/Services/Ai/GraphPipelineExecutor.cs` | Bucle principal de ejecucion concurrente (~400 lineas) | **Pendiente** |

### Que hara este archivo:
- Implementar `IPipelineExecutor`
- Recibir `IEnumerable<IModuleHandler>` via DI para resolver handlers por ModuleType
- **`ExecuteAsync`**: Cargar proyecto + conexiones, construir `ExecutionGraph`, inyectar userInput en nodo Start, ejecutar `RunGraphAsync`
- **`RunGraphAsync`**: Bucle principal con `Task.WhenAny`:
  1. Buscar nodos Ready (todos inputs satisfechos)
  2. Lanzar Task por cada Ready (llamar al handler correspondiente)
  3. `await Task.WhenAny(runningTasks)`
  4. Procesar resultado: Completed -> propagar outputs | Failed -> cascada | Paused -> continuar otros
  5. Repetir hasta que todo sea terminal
- **`FinalizeStep`**: Guardar StepExecution, ExecutionFile, OutputData en BD
- **`FinalizeExecution`**: Calcular estado final, generar summary, guardar en BD
- **`ResumeFromInteractionAsync`**: Restaurar PausedGraphState, inyectar respuesta, continuar RunGraphAsync
- **`ResumeFromCheckpointAsync`**: Similar, aprobar y propagar pass-through
- **`RetryFromModuleAsync`**: Restaurar outputs upstream, resetear modulo + downstream a Pending, re-ejecutar
- **`RetryFromStepAsync`**: Backward compat, busca moduleId por StepOrder, redirige a RetryFromModuleAsync
- Helpers: `MergeConfiguration`, `BuildPreviousSummaryContext`, `GenerateExecutionSummary` (extraidos del executor actual)

### Nota sobre concurrencia:
- Los handlers se ejecutan en paralelo (llamadas a APIs externas)
- El bucle principal es single-threaded (procesa completions uno a uno via WhenAny)
- Solo el bucle principal escribe a BD (handlers devuelven ModuleResult, no tocan DB)

---

## Fase 4: Integracion - PENDIENTE

### 4.1 Actualizar IPipelineExecutor.cs
**Archivo**: `Server/Services/Ai/IPipelineExecutor.cs`
**Cambios**:
- Anadir: `Task<ProjectExecution> RetryFromModuleAsync(Guid executionId, Guid moduleId, string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default)`
- Mantener: `RetryFromStepAsync` como backward compat (redirige internamente a RetryFromModuleAsync)
- Eliminar: `ResumeFromBranchInteractionAsync` (ya no hay branches)
- Eliminar: `ResumeFromOrchestratorAsync` (el orchestrator ya no pausa)

### 4.2 Registrar en DI (Program.cs)
**Archivo**: `Server/Program.cs` (linea ~69)
**Cambios**:
- Reemplazar: `builder.Services.AddTransient<IPipelineExecutor, PipelineExecutor>()` por `GraphPipelineExecutor`
- Anadir: Registro de los 18 handlers como `IModuleHandler`:
  ```csharp
  builder.Services.AddTransient<IModuleHandler, StartModuleHandler>();
  builder.Services.AddTransient<IModuleHandler, TextModuleHandler>();
  builder.Services.AddTransient<IModuleHandler, ImageModuleHandler>();
  // ... los 18 handlers
  ```

### 4.3 Simplificar save-graph endpoint
**Archivo**: `Server/Program.cs` (lineas ~889-1178)
**Eliminar**:
- Lineas ~889-939: Computacion de `InputMapping` (derivar source/field/outputKey de conexiones)
- Lineas ~941-1014: Computacion de `sceneInputs`, `templateInputs`, `fieldInputs`, `coordinatorInputs`
- Lineas ~1079-1178: Auto-deteccion de branches (`BranchId`, `BranchFromStep`)
**Mantener**:
- Lineas ~802-809: Guardar posiciones (PosX, PosY)
- Lineas ~812-839: Reemplazar conexiones en BD (ModuleConnection)
- Lineas ~841-888: Topological sort para StepOrder (solo display)
- Lineas ~1181+: Guardar SceneCounts y ModuleConfigs

### 4.4 Modulo Start en cliente
**Archivos a modificar**:

**`Client/Models/PipelineGraphModels.cs`** - Anadir en `GetPorts()`:
```csharp
case "Start":
    ports.Add(new("output_prompt", "Prompt", PortDataType.Text, isInput: false));
    break;
```
Anadir en `GetModuleIcon()`: `"Start" => "bi-play-circle"`
Anadir en `GetModuleColor()`: `"Start" => "#43a047"`

**`Client/Pages/Modules.razor`** - Anadir al catalogo:
```csharp
new("start", "Inicio", "System", ["Start"], ["start","entry-point","free"], "Punto de entrada del pipeline. Captura el prompt del usuario.")
```
Anadir al mapping de tipos: `"Start" => "Inicio del pipeline"`
Anadir al listado de tipos: `("Start", "Inicio", "S")`

**`Client/Components/Pipeline/PipelineCanvas.razor`** - Anadir Start a un grupo de paleta (ej: resources o un nuevo grupo "control")

### 4.5 Actualizar endpoints de ejecucion
**Archivo**: `Server/Program.cs`
**Cambios en endpoints**:
- El endpoint `POST /api/projects/{id}/execute` ya llama a `IPipelineExecutor.ExecuteAsync` - no necesita cambio (DI lo resuelve)
- El endpoint `POST /api/executions/{id}/retry-from-step` necesita adaptarse para usar RetryFromModuleAsync si se pasa moduleId
- Endpoints de resume (interaction, checkpoint) siguen igual (interfaz compatible)

---

## Fase 5: Limpieza - PENDIENTE

### 5.1 Deprecar PipelineExecutor.cs
**Archivo**: `Server/Services/Ai/PipelineExecutor.cs`
**Cambio**: Anadir `[Obsolete("Use GraphPipelineExecutor instead")]` en la clase
**No borrar**: Mantener como referencia/rollback hasta validar que todo funciona

### 5.2 Deprecar campos de ProjectModule
**Archivo**: `Server/Models/ProjectModule.cs`
**Cambios**:
- `BranchId`: Anadir `[Obsolete]` - ya no lo usa el nuevo executor
- `BranchFromStep`: Anadir `[Obsolete]` - ya no lo usa el nuevo executor
- `InputMapping`: Anadir `[Obsolete]` - reemplazado por ModuleConnection graph

### 5.3 Limpiar UI de branches
**Archivo**: `Client/Components/Pipeline/PipelineCanvas.razor`
**Cambio**: Eliminar cualquier logica que dependa de `BranchId` para visualizacion (los labels de paso A7, B5, etc. se simplifican a numeros)

---

## Orden de implementacion recomendado

1. **GraphPipelineExecutor.cs** (bloqueante para todo lo demas)
2. **IPipelineExecutor.cs** (actualizar interfaz)
3. **Program.cs** - DI registration (handlers + executor)
4. **Program.cs** - Simplificar save-graph
5. **Cliente** - Modulo Start (ModulePortRegistry + catalogo + paleta)
6. **Program.cs** - Actualizar endpoints si es necesario
7. **Limpieza** - [Obsolete] en PipelineExecutor y campos ProjectModule
