# Arquitectura PixelAgents

## Que es

PixelAgents es una aplicacion web para construir y ejecutar pipelines visuales de agentes IA.
El usuario conecta modulos reutilizables en un editor de grafo y lanza ejecuciones que pueden
producir texto, imagenes, video, audio, disenos y publicaciones en redes sociales, con soporte
para interacciones humanas via Telegram o WhatsApp. Stack: .NET 8 (Blazor WASM + ASP.NET Core
Minimal API + EF Core + SignalR) sobre PostgreSQL.

---

## Mapa de capas

```
Browser
  |
  |  Blazor WebAssembly (HTTP/cookie auth + SignalR WS)
  v
nginx (:80)
  |-- /            --> static files Blazor WASM (/var/www/html)
  |-- /api/        --> proxy HTTP  --> Server :5000
  |-- /hubs/       --> proxy WS    --> Server :5000  (SignalR)
  |-- /swagger     --> proxy HTTP  --> Server :5000
  v
ASP.NET Core Minimal API (:5000)
  |-- Identity + CoreDbContext  (DB: pixelagents_core)
  |-- TenantDbContextFactory    (DB: una por usuario/cuenta)
  |-- GraphPipelineExecutor     (motor de ejecucion de grafos)
  |-- IModuleHandler x18        (handlers por tipo de modulo)
  |-- IAiProvider x7            (OpenAI, Anthropic, Google, xAI,
  |                               LeonardoAI, Pexels, Json2Video)
  |-- ExecutionHub (SignalR)
  |-- TelegramPollingService    (hosted service)
  |-- SchedulerBackgroundService (hosted service, cron via Cronos)
  v
PostgreSQL :5432 (Docker interno) / :5433 (host)
```

---

## Componentes principales

| Componente                  | Carpeta / archivo                                      | Rol                                                               |
|-----------------------------|--------------------------------------------------------|-------------------------------------------------------------------|
| Client (Blazor WASM)        | `Client/`                                              | SPA; editor de grafo, configuracion, visualizacion de ejecuciones |
| Server (Minimal API)        | `Server/Program.cs`                                    | Todos los endpoints HTTP, DI, middlewares, arranque               |
| CoreDbContext               | `Server/Data/CoreDbContext.cs`                         | BD global: identidad, cuentas, correlaciones Telegram/WhatsApp    |
| UserDbContext               | `Server/Data/UserDbContext.cs`                         | BD por tenant: modulos, proyectos, ejecuciones, logs, reglas      |
| TenantDbContextFactory      | `Server/Services/TenantDbContextFactory.cs`            | Crea instancias de UserDbContext apuntando a la BD del usuario     |
| GraphPipelineExecutor       | `Server/Services/Ai/GraphPipelineExecutor.cs`          | Ejecuta el grafo de modulos; gestiona paralelismo y pausas        |
| ExecutionGraph              | `Server/Services/Ai/ExecutionGraph.cs`                 | Construye el grafo en memoria; propaga datos entre puertos         |
| ExecutionHub (SignalR)      | `Server/Hubs/ExecutionHub.cs`                          | Emite logs y progreso de ejecucion al cliente en tiempo real       |
| TelegramPollingService      | `Server/Services/Telegram/TelegramPollingService.cs`   | Hosted service; recibe respuestas de usuarios via Telegram         |
| SchedulerBackgroundService  | `Server/Services/Scheduler/SchedulerBackgroundService.cs` | Hosted service; lanza ejecuciones programadas con expresiones cron |

---

## Modelo de datos

**CoreDb** (`pixelagents_core`) es la base global compartida. Contiene las tablas de ASP.NET
Identity (`ApplicationUser`, `IdentityRole`, etc.), la entidad `Account` (una por usuario
registrado) y las tablas de correlacion `TelegramCorrelations` / `WhatsAppCorrelations`, que
almacenan el estado de interacciones externas pendientes de respuesta.

**UserDb** (nombre dinamico, uno por cuenta) almacena todos los datos funcionales del tenant:
`ApiKeys`, `AiModules`, `Projects`, `ProjectModules`, `ModuleConnections`, `ProjectExecutions`,
`StepExecutions`, `ExecutionFiles`, `ExecutionLogs`, `ProjectSchedules`, `OrchestratorOutputs`
y `Rules`. No hay migraciones EF formales: la BD se crea con `EnsureCreated` y los cambios de
esquema incrementales se aplican con `ExecuteSqlRaw` (`CREATE TABLE IF NOT EXISTS`,
`ALTER TABLE ... ADD COLUMN IF NOT EXISTS`) al arrancar el servidor.

---

## Flujo de ejecucion de un pipeline

1. El usuario pulsa "Ejecutar" en el cliente; el navegador llama a
   `POST /api/projects/{id}/execute`.
2. El servidor resuelve la BD tenant del usuario autenticado y llama a
   `GraphPipelineExecutor.ExecuteAsync`.
3. El executor carga el proyecto con todos sus `ProjectModules` y `ModuleConnections` desde
   la BD tenant.
4. `ExecutionGraph.Build` construye el grafo en memoria: nodos (modulos activos) y puertos
   conectados entre si.
5. Se marca como `Ready` el nodo `Start` (unico punto de entrada obligatorio).
6. El bucle `RunGraphAsync` despacha en paralelo todos los nodos `Ready`, cada uno ejecutado
   por su `IModuleHandler` correspondiente.
7. Al completarse un nodo, `CompleteNodeAndPrepareDownstream` propaga la salida a los puertos
   de entrada de los nodos siguientes; si todos sus puertos requeridos estan satisfechos, el
   nodo pasa a `Ready` y se ejecuta.
8. Los logs y cambios de estado se emiten en tiempo real via `SignalRExecutionLogger` hacia el
   cliente a traves de `ExecutionHub`.
9. Si un nodo es de tipo `Interaction` o `Checkpoint`, el executor pausa la ejecucion
   (`PausedStepData` serializado en la BD) y espera respuesta externa (Telegram, WhatsApp o
   UI). La ejecucion se reanuda con `ResumeFromInteractionAsync` o `ResumeFromCheckpointAsync`.
10. Al terminar todos los nodos, la ejecucion queda en `Completed`, `Failed` o `Cancelled`.

---

## Modulos soportados

| Tipo          | Handler                    | Descripcion breve                                          |
|---------------|----------------------------|------------------------------------------------------------|
| Start         | StartModuleHandler         | Punto de entrada; inyecta el input del usuario al grafo    |
| StaticText    | StaticTextModuleHandler    | Emite texto estatico configurado en el modulo              |
| FileUpload    | FileUploadModuleHandler    | Pasa archivos adjuntos al modulo como salida               |
| Text          | TextModuleHandler          | Genera texto con un proveedor LLM                          |
| Image         | ImageModuleHandler         | Genera imagenes con un proveedor de imagen                 |
| Video         | VideoModuleHandler         | Genera video con un proveedor de video                     |
| VideoEdit     | VideoEditModuleHandler     | Edita video existente via proveedor                        |
| VideoSearch   | VideoSearchModuleHandler   | Busca videos en proveedores externos (Pexels, etc.)        |
| Audio         | AudioModuleHandler         | Genera audio (TTS) con un proveedor                        |
| Transcription | TranscriptionModuleHandler | Transcribe audio a texto via proveedor                     |
| Embeddings    | EmbeddingsModuleHandler    | Genera embeddings de texto via proveedor                   |
| Scene         | SceneModuleHandler         | Agrupa campos estaticos y puertos en un objeto de escena   |
| Orchestrator  | OrchestratorModuleHandler  | Planifica salidas dinamicas y las enruta a nodos hijo      |
| Coordinator   | CoordinatorModuleHandler   | Combina y resume resultados de ramas anteriores            |
| Interaction   | InteractionModuleHandler   | Pausa el pipeline y espera respuesta humana (Telegram/WA)  |
| Checkpoint    | CheckpointModuleHandler    | Pausa para revision humana antes de continuar              |
| Design        | DesignModuleHandler        | Genera disenos via proveedor grafico (Canva, etc.)         |
| Publish       | PublishModuleHandler       | Publica contenido en Instagram o TikTok via Buffer API     |

---

## Proveedores IA

| Proveedor   | ProviderType | Modulos que sirve                              |
|-------------|--------------|------------------------------------------------|
| OpenAI      | `OpenAI`     | Text, Image (DALL-E), Video                    |
| Anthropic   | `Anthropic`  | Text (Claude)                                  |
| Google      | `Google`     | Text (Gemini), Image, Video                    |
| xAI         | `xAI`        | Text (Grok), Image                             |
| LeonardoAI  | `LeonardoAI` | Image                                          |
| Pexels      | `Pexels`     | VideoSearch                                    |
| Json2Video  | `Json2Video` | VideoEdit                                      |

---

## API HTTP — endpoints clave

| Grupo        | Patron base                                       | Notas                                            |
|--------------|---------------------------------------------------|--------------------------------------------------|
| Auth         | `POST /api/auth/register|login|logout`            | Cookie-based; `GET /api/auth/me`                 |
| ApiKeys      | `GET|POST|PUT|DELETE /api/apikeys`                | Credenciales por proveedor, almacenadas por tenant |
| Rules        | `GET|POST|PUT|DELETE /api/rules`                  | Reglas obligatorias inyectadas en cada ejecucion  |
| Modules      | `GET|POST|PUT|DELETE /api/modules`                | Definiciones de modulos reutilizables + archivos  |
| Projects     | `GET|POST|PUT|DELETE /api/projects`               | Pipeline; incluye graph save y duplicar           |
| Executions   | `POST /api/projects/{id}/execute`                 | Lanza ejecucion                                   |
|              | `POST /api/projects/{id}/cancel`                  | Cancela ejecucion activa                          |
|              | `POST /api/executions/{id}/retry-from-module`     | Reintenta desde un nodo concreto del grafo        |
|              | `GET /api/executions/{id}/logs`                   | Logs persistidos; progreso en tiempo real por SignalR |
| Webhooks     | `POST /api/webhooks/whatsapp|telegram`            | Recepcion de respuestas externas para reanudar interacciones |
| Schedules    | `GET|POST|PUT|DELETE /api/projects/{id}/schedule` | Cron con Cronos; timezone configurable            |
| SignalR      | `/hubs/execution`                                 | Canal de logs y progreso de ejecucion en tiempo real |
| Archivos     | `GET /api/executions/{id}/files/{fileId}`         | Descarga de archivos generados                    |
| Build info   | `GET /api/build-info`                             | Version y commit del build                        |

---

## Como arrancar en local (modo rapido)

Requiere PostgreSQL en `localhost:5432` con usuario `postgres`.

```bash
# 1. Situarse en la raiz del repositorio clonado

# 2. Configurar connection strings (variables de entorno o appsettings.Development.json)
export ConnectionStrings__Core="Host=localhost;Port=5432;Database=pixelagents_core;Username=postgres;Password=TU_PASSWORD"
export ConnectionStrings__TenantTemplate="Host=localhost;Port=5432;Database={db};Username=postgres;Password=TU_PASSWORD"

# 3. Arrancar el servidor (crea la BD Core en el primer arranque via EnsureCreated)
dotnet run --project Server/

# 4. En otra terminal, servir el cliente (opcional; en produccion el servidor sirve los estaticos)
dotnet run --project Client/

# 5. Con Docker Compose — requiere definir variables de entorno antes de levantar:
#    Variables obligatorias: POSTGRES_PASSWORD, PUBLIC_IP
#    Variables opcionales:   PG_PORT (def. 5433), APP_PORT (def. 8080),
#                            DOZZLE_PORT (def. 9999), DOZZLE_USERNAME, DOZZLE_PASSWORD, DOZZLE_KEY
export POSTGRES_PASSWORD=TU_PASSWORD
export PUBLIC_IP=TU_IP_PUBLICA
docker compose up -d
```

---

## Donde profundizar

- `README.md` — guia tecnica completa de onboarding
- `docs/MIGRATION_PLAN_GRAPH_EXECUTOR.md` — decision de diseno del executor basado en grafo
- `docs/MIGRATION_PROGRESS.md` — historial de la migracion al executor actual
- `Server/Services/Ai/GraphPipelineExecutor.cs` — logica de ejecucion, pausas, reintentos y publicacion
- `Server/Services/Ai/ExecutionGraph.cs` — construccion del grafo, propagacion de puertos y fallos en cascada
- `Server/Services/Ai/Handlers/` — un archivo por tipo de modulo
- `Server/Program.cs` — todos los endpoints y configuracion DI
