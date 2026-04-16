# Progreso de Migración: Graph Pipeline Executor

## Estado Actual: Fase 1-2 completadas, Fase 3 en progreso

---

## Fase 1: Infraestructura base - COMPLETADA

### Archivos creados:
| Archivo | Lineas | Estado |
|---------|--------|--------|
| `Server/Services/Ai/ModuleNode.cs` | 83 | Completado |
| `Server/Services/Ai/ExecutionGraph.cs` | 180 | Completado |
| `Server/Services/Ai/PortDataResolver.cs` | 135 | Completado |
| `Server/Services/Ai/PausedGraphState.cs` | 75 | Completado |
| `Server/Services/Ai/Handlers/IModuleHandler.cs` | 110 | Completado |

### Descripcion:
- **ModuleNode.cs**: Estructuras de datos runtime - `ModuleNode`, `InputPort`, `OutputPort`, `PortConnection`, `PortData`. Cada nodo tiene estado (`Pending -> Ready -> Running -> Completed/Failed/Paused`), puertos de entrada/salida, y tracking de dependencias.
- **ExecutionGraph.cs**: Construye el grafo desde `ProjectModule` + `ModuleConnection` de la BD. Detecta nodos ready (`GetReadyNodes`), propaga outputs a downstream (`PropagateOutputs`), cascada de fallos (`CascadeFailure`), y traverse de downstream para retry (`GetDownstreamNodes`).
- **PortDataResolver.cs**: Mapea `StepOutput` a `PortData` por cada output port. Maneja outputs indexados de Orchestrator (`output_1` -> `Items[0]`), pass-through de Checkpoint, imagenes indexadas, y tipos estandar.
- **PausedGraphState.cs**: Serializa/deserializa el estado del grafo para pause/resume (Interaction, Checkpoint).
- **IModuleHandler.cs**: Interfaz `IModuleHandler`, `ModuleExecutionContext` (con helpers para leer inputs/config), `ModuleResult` (con factory methods), `ProducedFile`, `ModuleFileInfo`.

---

## Fase 2: Module Handlers - COMPLETADA

### 18 handlers creados:
| Handler | Tipo | AI? | Lineas | Descripcion |
|---------|------|-----|--------|-------------|
| `StartModuleHandler` | Start | No | 25 | Emite prompt del usuario (entrada del pipeline) |
| `StaticTextModuleHandler` | StaticText | No | 20 | Emite texto fijo configurado |
| `FileUploadModuleHandler` | FileUpload | No | 45 | Copia archivos adjuntos al workspace |
| `SceneModuleHandler` | Scene | No | 65 | Construye JSON escena desde valores + inputs |
| `TextModuleHandler` | Text | Si | 60 | Generacion de texto con AI |
| `ImageModuleHandler` | Image | Si | 110 | Generacion de imagenes (multi-imagen, img2img) |
| `VideoModuleHandler` | Video | Si | 75 | Generacion de video desde prompt |
| `VideoEditModuleHandler` | VideoEdit | Si | 160 | Composicion video (escenas + template mode) |
| `VideoSearchModuleHandler` | VideoSearch | Si | 60 | Busqueda de video en Pexels |
| `AudioModuleHandler` | Audio | Si | 55 | Text-to-Speech |
| `TranscriptionModuleHandler` | Transcription | Si | 50 | Speech-to-Text |
| `EmbeddingsModuleHandler` | Embeddings | Si | 45 | Generacion de embeddings |
| `OrchestratorModuleHandler` | Orchestrator | Si | 100 | Multi-output planning con AI |
| `CoordinatorModuleHandler` | Coordinator | Si | 70 | Agrega inputs + AI |
| `CheckpointModuleHandler` | Checkpoint | No | 35 | Pausa para revision humana |
| `InteractionModuleHandler` | Interaction | No | 30 | Mensajeria con pausa |
| `DesignModuleHandler` | Design | Si | 65 | Creacion de disenos (Canva) |
| `PublishModuleHandler` | Publish | No | 20 | Publicacion en redes sociales |

### Principio clave:
Cada handler es autocontenido: recibe `InputsByPort` pre-resuelto, devuelve `ModuleResult`. No hay logica de branches, no hay duplicacion, no hay switch/case por contexto.

---

## Fase 3: GraphPipelineExecutor - EN PROGRESO

### Lo que falta crear:
| Archivo | Descripcion | Estado |
|---------|-------------|--------|
| `Server/Services/Ai/GraphPipelineExecutor.cs` | Bucle principal con Task.WhenAny | **Pendiente** |

### Algoritmo del executor:
```
1. Cargar proyecto + conexiones de BD
2. Construir ExecutionGraph
3. Bucle: encontrar nodos Ready -> lanzar Tasks -> WhenAny -> procesar resultado
4. Propagar outputs -> buscar nuevos Ready -> repetir
5. Finalizar cuando todo sea terminal
```

---

## Fase 4: Integracion - PENDIENTE

| Tarea | Descripcion | Estado |
|-------|-------------|--------|
| Actualizar `IPipelineExecutor.cs` | Anadir `RetryFromModuleAsync`, simplificar resume | Pendiente |
| Registrar en `Program.cs` | DI de handlers + executor, actualizar endpoints | Pendiente |
| Simplificar save-graph | Eliminar InputMapping, branches, sceneInputs, etc. | Pendiente |
| Modulo Start (cliente) | Anadir a ModulePortRegistry, catalogo, paleta | Pendiente |

---

## Fase 5: Limpieza - PENDIENTE

| Tarea | Descripcion | Estado |
|-------|-------------|--------|
| Deprecar `PipelineExecutor.cs` | Marcar [Obsolete], mantener para rollback | Pendiente |
| Deprecar campos BD | BranchId, BranchFromStep, InputMapping -> nullable | Pendiente |
| Limpiar branches en UI | Eliminar logica de branches en PipelineCanvas | Pendiente |

---

## Resumen de Metricas

| Metrica | Antes | Despues (estimado) |
|---------|-------|-------------------|
| Archivo principal | PipelineExecutor.cs (7564 lineas) | GraphPipelineExecutor.cs (~400 lineas) |
| Handlers | Todo en 1 archivo, duplicado 5x | 18 archivos, 30-160 lineas cada uno |
| Save-graph | ~400 lineas logica | ~80 lineas |
| Total codigo nuevo | - | ~2200 lineas en 25 archivos |
| Duplicacion de logica | Alta (main/branch/retry/resume) | Cero |
| Ejecucion concurrente | No | Si (Task.WhenAny) |
| Branches | Manual (BranchId, deferred, etc.) | Automatico (grafo de dependencias) |
