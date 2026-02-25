# PixelAgents - AI Content Creation Studio

Sistema modular de agentes de IA representados como avatares pixel art en una oficina virtual. Cada agente tiene habilidades especificas y trabajan en coordinacion para crear contenido para redes sociales.

## Arquitectura

```
PixelAgents/
├── src/
│   ├── Server/                          # Backend API (.NET 8)
│   │   ├── PixelAgents.Api/             # ASP.NET Core Web API + SignalR Hub
│   │   ├── PixelAgents.Application/     # CQRS handlers (MediatR)
│   │   ├── PixelAgents.Domain/          # Entidades, Value Objects, Interfaces
│   │   ├── PixelAgents.Infrastructure/  # EF Core, Repositorios, Servicios
│   │   └── PixelAgents.AgentSystem/     # Sistema de plugins/modulos
│   │
│   ├── Client/                          # Frontend (Blazor WASM)
│   │   └── PixelAgents.Web/             # UI Pixel Art con SignalR
│   │
│   ├── Modules/                         # Modulos de IA (cada uno es un agente)
│   │   ├── PixelAgents.Module.TrendScout/      # Investigador de tendencias
│   │   ├── PixelAgents.Module.TemplateForge/   # Disenador de plantillas
│   │   ├── PixelAgents.Module.ContentWeaver/   # Ensamblador de contenido
│   │   └── PixelAgents.Module.ScheduleMaster/  # Estratega de publicacion
│   │
│   └── Shared/                          # DTOs, contratos, enums compartidos
│       └── PixelAgents.Shared/
│
└── tests/                               # Tests unitarios
```

## Agentes (Avatares)

| Avatar | Modulo | Rol | Descripcion |
|--------|--------|-----|-------------|
| Scout | `trend-scout` | Trend Researcher | Analiza redes sociales para encontrar temas relevantes |
| Forja | `template-forge` | Template Designer | Crea plantillas de Photoshop optimizadas por plataforma |
| Weaver | `content-weaver` | Content Assembler | Ensambla contenido final combinando plantillas + datos |
| Chrono | `schedule-master` | Publishing Strategist | Optimiza horarios de publicacion por plataforma |

## Stack Tecnologico

**Server:**
- .NET 8 / ASP.NET Core Web API
- Entity Framework Core + SQLite
- MediatR (CQRS)
- SignalR (comunicacion en tiempo real)
- Serilog (logging)
- Swagger/OpenAPI

**Client:**
- Blazor WebAssembly
- SignalR Client
- CSS puro con tema pixel art

**AI (preparado para integracion):**
- Microsoft Semantic Kernel
- Arquitectura de plugins extensible via `IAgentModule`

## Como funciona

1. **Crear un Workspace** - Se crea una oficina virtual con todos los agentes registrados
2. **Crear un Content Project** - Se define un tema y las plataformas destino
3. **Pipeline automatico** - Los agentes ejecutan en secuencia:
   - Scout investiga tendencias del tema
   - Forja crea plantillas visuales
   - Weaver ensambla el contenido final
   - Chrono programa la mejor hora de publicacion
4. **Tiempo real** - SignalR transmite el progreso al cliente, animando los avatares

## API Endpoints

```
POST   /api/workspaces              # Crear workspace (oficina)
GET    /api/workspaces/{id}         # Obtener workspace con agentes
GET    /api/workspaces/{id}/agents  # Listar agentes del workspace

POST   /api/content/projects        # Crear proyecto de contenido
POST   /api/pipelines/execute       # Ejecutar pipeline
GET    /api/pipelines/{id}          # Estado del pipeline

GET    /api/modules                 # Modulos registrados

WS     /hubs/agents                 # SignalR Hub (tiempo real)
```

## Crear un nuevo modulo

Para agregar un nuevo agente, crea una clase que herede de `AgentModuleBase`:

```csharp
public class MyCustomModule : AgentModuleBase
{
    public override string ModuleKey => "my-module";
    public override string DisplayName => "Custom Agent";
    // ... definir skills, apariencia, personalidad

    public override async Task<AgentModuleResult> ExecuteAsync(
        AgentModuleContext context, CancellationToken ct)
    {
        // Tu logica de IA aqui
        return AgentModuleResult.Ok(outputData, "Summary");
    }
}
```

Registralo en `Program.cs`:
```csharp
builder.Services.AddAgentModule<MyCustomModule>();
```

## Requisitos

- .NET 8 SDK
- Node.js (opcional, para herramientas de desarrollo)

## Ejecutar

```bash
# Server (desde src/Server/PixelAgents.Api/)
dotnet run

# Client (desde src/Client/PixelAgents.Web/)
dotnet run
```
