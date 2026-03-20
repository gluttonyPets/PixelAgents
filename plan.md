# Plan: Orquestador con Salidas Fijas (sin libre albedrío)

## Resumen

Reemplazar el orquestador dinámico (que decide libremente qué módulos usar) por uno con **salidas fijas configuradas por el usuario**. Cada salida es un "enganche" visual que se conecta a un módulo existente, con su propio prompt que define qué debe producir el orquestador para esa salida. Todas las salidas se ejecutan en paralelo una vez el orquestador genera el contenido.

## Concepto

```
[Guionizador] → [Orquestador]
                    ├── Salida 1 (prompt: "busca video de perro paseando") → [Módulo Pexels]
                    ├── Salida 2 (prompt: "busca video de perro limpiándose") → [Módulo Pexels]
                    ├── Salida 3 (prompt: "genera video de gato cepillado") → [Módulo Google Video]
                    └── Salida 4 (prompt: "genera la voz en off") → [Módulo Audio]
```

El paso ANTERIOR al orquestador recibe en su contexto la estructura de salidas, para que adapte su respuesta a lo que el orquestador necesita.

---

## 1. Modelo de datos

**Nuevo entity: `OrchestratorOutput`** (`Server/Models/OrchestratorOutput.cs`)

```csharp
public class OrchestratorOutput
{
    public Guid Id { get; set; }
    public Guid ProjectModuleId { get; set; }  // ProjectModule del orquestador
    public string OutputKey { get; set; }       // "output_1", "output_2"
    public string Label { get; set; }           // "Video perro paseando"
    public string Prompt { get; set; }          // "Busca un vídeo de un perro..."
    public int SortOrder { get; set; }
    public Guid? TargetModuleId { get; set; }   // AiModule destino (se asigna al conectar)
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    // Navigation
    public ProjectModule ProjectModule { get; set; }
    public AiModule? TargetModule { get; set; }
}
```

- Añadir `ICollection<OrchestratorOutput> OrchestratorOutputs` a `ProjectModule`
- Migración EF para crear tabla `OrchestratorOutputs`

## 2. API Endpoints

**CRUD de salidas del orquestador:**

- `GET /api/projects/{pid}/modules/{mid}/orchestrator-outputs`
- `POST /api/projects/{pid}/modules/{mid}/orchestrator-outputs`
- `PUT /api/projects/{pid}/modules/{mid}/orchestrator-outputs/{id}`
- `DELETE /api/projects/{pid}/modules/{mid}/orchestrator-outputs/{id}`

**DTOs:**
```csharp
record OrchestratorOutputRequest(string Label, string Prompt, int SortOrder, Guid? TargetModuleId);
record OrchestratorOutputResponse(Guid Id, string OutputKey, string Label, string Prompt,
    int SortOrder, Guid? TargetModuleId, string? TargetModuleName, string? TargetModuleType);
```

## 3. OrchestratorSchema.cs — Nuevo prompt y schema

Reescribir `BuildOrchestratorPrompt` para que reciba las salidas configuradas en vez de módulos disponibles:

```
"Tienes las siguientes salidas que debes rellenar. Para cada outputKey, genera el contenido
basándote en el input recibido y el prompt específico de cada salida."
```

**Nuevo schema de respuesta:**
```json
{
  "summary": "Resumen",
  "outputs": [
    { "outputKey": "output_1", "content": "texto generado para esta salida" }
  ]
}
```

**Nuevos modelos:**
- `OrchestratorPlanV2` con `List<OrchestratorOutputResult>`
- Mantener los viejos temporalmente para no romper nada

## 4. PipelineExecutor.cs — HandleOrchestratorStepAsync

**Reescribir completamente:**

1. Cargar `OrchestratorOutputs` del ProjectModule (con TargetModule)
2. Validar que hay salidas configuradas
3. Resolver input del paso anterior
4. Llamar al AI con nuevo prompt (salidas definidas + prompt genérico)
5. Parsear respuesta → extraer contenido para cada outputKey
6. **Ejecutar todas las salidas en paralelo** con `Task.WhenAll`:
   - Para cada output con TargetModule asignado → ejecutar módulo con el contenido como input
7. Combinar resultados en StepOutput
8. **Sin pausa de revisión** — ejecución directa

## 5. Inyectar estructura al paso anterior

Cuando el SIGUIENTE paso es un orquestador, inyectar en el contexto del paso actual:

```
"IMPORTANTE: Tu respuesta será procesada por un orquestador que tiene estas tareas definidas:
1. [Label]: [Prompt]
2. [Label]: [Prompt]
...
Estructura tu respuesta para cubrir todas estas necesidades de forma clara y separada."
```

**Dónde:** En `InitialExecuteAsync`, `ResumeFromInteractionAsync`, y retry path, antes de construir el `AiExecutionContext` de pasos Text. Comprobar si `nextModule` es orquestador y cargar sus outputs.

## 6. Frontend — Puertos dinámicos del orquestador

**`PipelineGraphModels.cs` → `ModulePortRegistry.GetPorts()`:**

- Para tipo "Orchestrator": generar puertos de salida dinámicos basados en OrchestratorOutputs
- Cada salida = un puerto de salida con tipo `Any` (el tipo real depende del TargetModule)
- El puerto de entrada sigue siendo `input_prompt`

## 7. Frontend — UI de configuración

**`ProjectDetail.razor`:** Al editar un paso orquestador, mostrar sección de salidas:
- Lista editable: Label + Prompt + Módulo destino (dropdown) por cada salida
- Botones añadir/eliminar salida

**`PipelineCanvas.razor` / `PipelineNode.razor`:**
- Nodos orquestador muestran múltiples puertos de salida (uno por OrchestratorOutput)
- Conexiones visuales desde cada puerto al módulo destino

## 8. Frontend — ApiService

```csharp
GetOrchestratorOutputsAsync(projectId, moduleId)
CreateOrchestratorOutputAsync(projectId, moduleId, request)
UpdateOrchestratorOutputAsync(projectId, moduleId, outputId, request)
DeleteOrchestratorOutputAsync(projectId, moduleId, outputId)
```

## Qué se elimina

- `ExecuteOrchestratorPlanAsync` — el orquestador ya no decide módulos
- `ResumeFromOrchestratorAsync` — ya no hay pausa de revisión
- Endpoint `/api/executions/{id}/orchestrator-review`
- `OrchestratorReviewRequest`, `AvailableModule`, `OrchestratorFeedback`, `OrchestratorComment`
- La lógica de auto-approve y feedback en `HandleOrchestratorStepAsync`

## Orden de implementación

1. Modelo DB + Migración
2. API endpoints CRUD
3. OrchestratorSchema nuevo
4. PipelineExecutor reescrito
5. Inyección al paso anterior
6. Frontend ApiService
7. Frontend UI (configuración + puertos dinámicos)
