# Plan: Editor Visual de Pipeline (Node-Based)

## Resumen
Reemplazar la vista lineal del pipeline en ProjectDetail.razor con un editor visual de nodos estilo Node-RED/Unreal Blueprints, donde los módulos son tarjetas con puertos de entrada/salida tipados, y las conexiones se hacen visualmente arrastrando cables entre puertos.

## Arquitectura

### Enfoque técnico
- **Pure Blazor + SVG** para las conexiones (sin dependencias JS externas)
- **JS Interop mínimo** solo para: drag de nodos, coordenadas del mouse durante conexión
- **Nuevo componente** `PipelineCanvas.razor` que reemplaza el tab Pipeline actual
- **Datos**: posiciones de nodos y conexiones almacenadas en `Configuration` del proyecto como JSON

### Modelo de datos del grafo

```csharp
// Nuevo DTO para el grafo visual
public class PipelineGraph
{
    public List<PipelineNode> Nodes { get; set; } = [];
    public List<PipelineConnection> Connections { get; set; } = [];
}

public class PipelineNode
{
    public Guid ModuleId { get; set; }      // Reference to ProjectModule
    public double X { get; set; }
    public double Y { get; set; }
}

public class PipelineConnection
{
    public Guid FromModuleId { get; set; }
    public string FromPort { get; set; }     // e.g. "output", "scene_1_video"
    public Guid ToModuleId { get; set; }
    public string ToPort { get; set; }       // e.g. "input_text", "input_video"
}
```

### Definición de puertos por tipo de módulo

Cada tipo de módulo define sus puertos de entrada/salida:

| ModuleType | Input Ports | Output Ports |
|------------|-------------|--------------|
| **Text** | `prompt` (texto) | `text_output` (texto) |
| **Image** | `prompt` (texto) | `image_output` (imagen) |
| **Video** | `prompt` (texto), `image_ref` (imagen, opcional) | `video_output` (video) |
| **VideoSearch** | `query` (texto) | `videos_output` (video[]) |
| **VideoEdit (Json2Video)** | `scene_N_video` (video) × N escenas, `scene_N_script` (texto) × N escenas, `global_config` (config) | `video_output` (video) |
| **Audio** | `text` (texto) | `audio_output` (audio) |
| **Orchestrator** | `prompt` (texto) | `plan_output` (texto) |
| **Design** | `prompt` (texto) | `design_output` (archivo) |
| **Publish** | `content` (any) | `result` (texto) |
| **Interaction** | `message` (texto) | `response` (texto) |

### Tipos de puerto (para colores y validación)

```
text     → Azul (#6c63ff)
image    → Verde (#4caf50)
video    → Naranja (#ff9800)
video[]  → Naranja con borde doble
audio    → Rosa (#e91e63)
file     → Gris (#888)
config   → Amarillo (#ffc107)
any      → Blanco (#e0e0e0)
```

## Implementación por fases

### Fase 1: Infraestructura del canvas (archivos nuevos)

**1.1 JS Interop** — `wwwroot/js/pipeline-editor.js`
- `initCanvas(dotNetRef)`: Setup de event listeners para drag
- `startNodeDrag(nodeId, offsetX, offsetY)`: Tracking de posición durante drag
- `getMousePosition(canvasElement, event)`: Coordenadas relativas al canvas
- `startConnectionDrag(portElement)`: Tracking visual del cable temporal

**1.2 Modelos del grafo** — `Client/Models/PipelineGraphModels.cs`
- `PipelineGraph`, `PipelineNode`, `PipelineConnection`
- `PortDefinition` (Name, Label, DataType, Direction, IsRequired)
- `ModulePortRegistry` — clase estática que mapea ModuleType → puertos

**1.3 CSS** — Añadir estilos a `app.css`
- `.pipeline-canvas` — contenedor con scroll, grid de fondo
- `.pipeline-node` — tarjeta draggable con header coloreado por tipo
- `.pipeline-port` — círculos de conexión con colores por tipo de dato
- `.pipeline-port-label` — etiquetas descriptivas junto a cada puerto
- `.pipeline-connection` — SVG path con curva bezier
- `.pipeline-connection.temp` — línea temporal durante arrastre

### Fase 2: Componente PipelineCanvas

**2.1** `Client/Components/Pipeline/PipelineCanvas.razor`
- Canvas principal con SVG overlay
- Renderiza nodos y conexiones
- Maneja: agregar nodo (desde panel lateral), mover nodo, conectar/desconectar
- Zoom y pan (stretch goal)

**2.2** `Client/Components/Pipeline/PipelineNode.razor`
- Tarjeta individual del módulo
- Header con nombre e icono del tipo
- Puertos de entrada (lado izquierdo) con etiquetas
- Puertos de salida (lado derecho) con etiquetas
- Para Json2Video: muestra N puertos de escena según configuración
- Botón de configuración inline
- Indicador de estado (conectado/desconectado)

**2.3** `Client/Components/Pipeline/ConnectionRenderer.razor`
- Renderiza SVG paths (curvas Bézier) entre puertos conectados
- Color del cable = tipo de dato
- Línea temporal durante arrastre de conexión
- Click en cable para desconectar

### Fase 3: Integración en ProjectDetail

**3.1** Reemplazar el contenido del tab "Pipeline" con `<PipelineCanvas>`
- Mantener la funcionalidad existente de ejecución intacta
- El canvas comunica cambios via EventCallbacks al parent
- Guardar posiciones/conexiones en el proyecto (nuevo campo o dentro de Configuration)

**3.2** Panel lateral de módulos disponibles
- Lista de módulos del usuario filtrable
- Drag desde panel al canvas para agregar
- O click para agregar en posición automática

**3.3** Panel de configuración de nodo
- Al hacer click en un nodo, se abre panel lateral derecho
- Muestra config específica del módulo (reutilizar UI existente del step config)
- Para Json2Video: slider/input para número de escenas → actualiza puertos dinámicamente

### Fase 4: Backend

**4.1** Nuevo campo en Project o ProjectModule para almacenar layout del grafo
- Opción A: Campo `GraphLayout` en Project (JSON con posiciones)
- Opción B: Campos `PositionX`, `PositionY` en ProjectModule + tabla de conexiones

Recomiendo **Opción A** por simplicidad — el layout es metadata visual, no lógica de negocio.

**4.2** Endpoints:
- `PUT /api/projects/{id}/graph` — Guardar layout del grafo completo
- `GET /api/projects/{id}/graph` — Obtener layout

**4.3** Mapper: Convertir grafo visual → pipeline ejecutable
- Las conexiones definen el InputMapping de cada step
- El orden topológico de las conexiones define StepOrder
- Las ramas se detectan automáticamente por la topología

### Fase 5: Json2Video específico

**5.1** Puerto dinámico por escenas
- Config del nodo Json2Video tiene `sceneCount` (1-10)
- Por cada escena se generan 2 input ports: `scene_N_video` y `scene_N_script`
- El usuario conecta un VideoSearch/Video output → scene_N_video
- El usuario conecta un Text output → scene_N_script

**5.2** Actualizar PipelineExecutor
- Leer conexiones del grafo para resolver inputs de cada escena
- Construir el JSON de escenas dinámicamente según las conexiones

## Orden de implementación

1. **Modelos + CSS** (PipelineGraphModels.cs, estilos del canvas)
2. **JS Interop** (pipeline-editor.js, drag & connect)
3. **PipelineNode.razor** (tarjeta con puertos estáticos)
4. **PipelineCanvas.razor** (canvas + SVG + render de nodos)
5. **ConnectionRenderer** (cables SVG entre puertos)
6. **Integración ProjectDetail** (reemplazar tab Pipeline)
7. **Panel lateral** (módulos disponibles + config de nodo)
8. **Backend endpoints** (guardar/cargar grafo)
9. **Mapper grafo→pipeline** (convertir visual a ejecutable)
10. **Json2Video dinámico** (puertos por escena)
11. **Polish** (animaciones, validación visual, UX)

## Notas
- El pipeline existente (lineal) se puede migrar automáticamente al formato visual asignando posiciones X incrementales
- La ejecución sigue usando el modelo existente — solo cambia cómo se configura
- Mantener compatibilidad: si no hay grafo guardado, mostrar vista lineal legacy
