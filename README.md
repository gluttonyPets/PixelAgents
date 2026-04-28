# PixelAgents

Guia tecnica de onboarding del proyecto en su estado actual.

PixelAgents es una aplicacion web para construir, ejecutar y revisar pipelines
visuales de agentes IA. El usuario configura proveedores, crea modulos
reutilizables, los conecta en un editor de grafo y ejecuta proyectos que pueden
generar texto, imagen, video, audio, disenos, publicaciones sociales e
interacciones humanas mediante Telegram o WhatsApp.

La solucion esta hecha con .NET 8:

- `Client`: cliente Blazor WebAssembly.
- `Server`: API ASP.NET Core Minimal API, SignalR, EF Core y servicios de
  ejecucion.
- `Data`: proyecto .NET vacio en el estado actual.
- `docs`: documentacion historica de migracion del executor de pipelines.

Las carpetas `bin`, `obj`, `.git`, `Server/GeneratedMedia` y `Server/storage`
no forman parte del codigo fuente funcional. `GeneratedMedia` y `storage` son
artefactos de ejecucion y datos subidos/generados.

## Producto

El flujo principal es:

1. El usuario se registra o inicia sesion.
2. La aplicacion crea una cuenta y una base de datos tenant para ese usuario.
3. El usuario guarda API keys para proveedores externos.
4. El usuario crea modulos IA o modulos de sistema.
5. El usuario crea proyectos y conecta modulos en un canvas visual.
6. El servidor ejecuta el grafo con `GraphPipelineExecutor`.
7. Los logs, progreso y estados llegan al cliente en tiempo real por SignalR.
8. Los archivos producidos quedan asociados a ejecuciones y pueden descargarse.

Entidades funcionales principales:

- `Account` y `ApplicationUser`: identidad, cuenta y tenancy.
- `ApiKey`: credenciales por proveedor, guardadas por tenant.
- `AiModule`: definicion reutilizable de un modulo.
- `Project`: pipeline editable.
- `ProjectModule`: instancia de un `AiModule` dentro de un proyecto.
- `ModuleConnection`: arista entre puertos del grafo.
- `ProjectExecution`: ejecucion de un proyecto.
- `StepExecution`: ejecucion de un modulo dentro de una ejecucion.
- `ExecutionFile`: archivo producido o asociado a un paso.
- `ExecutionLog`: logs persistidos y emitidos por SignalR.
- `ProjectSchedule`: ejecuciones programadas con Cronos.
- `Rule`: reglas obligatorias inyectadas en ejecuciones.
- `TelegramCorrelation` y `WhatsAppCorrelation`: correlacion de respuestas
  externas para reanudar interacciones pausadas.

## Arquitectura

```text
Browser
  |
  | Blazor WebAssembly
  v
Client/
  |
  | HTTP + cookie auth + SignalR
  v
Server/ ASP.NET Core Minimal API
  |-- Identity + CoreDbContext
  |-- TenantDbContextFactory + UserDbContext
  |-- GraphPipelineExecutor
  |-- AI providers and module handlers
  |-- SignalR ExecutionHub
  |-- hosted services: Telegram polling and Scheduler
  v
PostgreSQL

Docker runtime:
nginx sirve Blazor, proxy /api, /hubs y /swagger hacia el server .NET.
```

### Cliente

`Client` es un proyecto Blazor WebAssembly (`Microsoft.NET.Sdk.BlazorWebAssembly`).
Su `Program.cs` configura:

- componente raiz `App`.
- `HeadOutlet`.
- `HttpClient` con `ApiBaseUrl` desde `wwwroot/appsettings*.json`.
- `ApiClient` para acceso HTTP.
- `AuthStateService` para estado de sesion.

Rutas actuales:

- `/`: registro, login y estado de usuario.
- `/apikeys`: gestion de claves de proveedor.
- `/rules`: reglas obligatorias por tenant.
- `/modules`: catalogo y configuracion de modulos.
- `/biblioteca`: archivos subidos a modulos.
- `/projects`: listado y creacion de proyectos.
- `/projects/{ProjectId:guid}`: detalle, editor visual, ejecuciones e
  integraciones del proyecto.

Componentes clave:

- `Client/Components/Pipeline/PipelineCanvas.razor`: editor visual del grafo,
  inspector de nodos, configuraciones y logs.
- `Client/Components/Pipeline/PipelineNode.razor`: render de un nodo y sus
  puertos.
- `Client/Models/PipelineGraphModels.cs`: tipos de puertos, compatibilidad,
  registro de puertos por tipo de modulo, iconos y colores.
- `Client/Models/Dtos.cs`: contratos usados por el cliente para hablar con la
  API.
- `Client/Services/ApiClient.cs`: wrapper de endpoints.

### Servidor

`Server` es un proyecto ASP.NET Core Web (`Microsoft.NET.Sdk.Web`) con Minimal
API en `Server/Program.cs`. En el estado actual ese archivo concentra:

- configuracion de Kestrel y limite de uploads de 512 MB.
- EF Core con PostgreSQL.
- ASP.NET Core Identity.
- autenticacion por cookie.
- CORS para el cliente Blazor.
- registro de proveedores IA, handlers, executor y servicios externos.
- Swagger.
- migraciones ligeras con SQL manual para algunas tablas existentes.
- endpoints HTTP.
- hub SignalR `/hubs/execution`.

Servicios registrados relevantes:

- `IAccountService -> AccountService`.
- `ITenantDbContextFactory -> TenantDbContextFactory`.
- `IAiProvider` para OpenAI, Anthropic, Leonardo, Gemini, Pexels, Json2Video y
  Grok.
- `IAiProviderRegistry -> AiProviderRegistry`.
- `IModuleHandler` para cada tipo de modulo soportado.
- `IPipelineExecutor -> GraphPipelineExecutor`.
- `ExecutionCancellationService`.
- `IExecutionLogger -> SignalRExecutionLogger`.
- hosted services `TelegramPollingService` y `SchedulerBackgroundService`.

### Base De Datos

El servidor usa dos contextos EF Core:

- `CoreDbContext`: base global. Hereda de `IdentityDbContext<ApplicationUser>`.
  Contiene usuarios, roles, `Accounts`, `WhatsAppCorrelations` y
  `TelegramCorrelations`.
- `UserDbContext`: base por tenant. Contiene claves, modulos, proyectos,
  conexiones, ejecuciones, logs, archivos, schedules, salidas de orquestador y
  reglas.

El tenancy se resuelve desde el usuario autenticado. Durante el registro,
`AccountService` crea una cuenta y una base tenant. Luego los endpoints usan
`ResolveTenantDb` para abrir el `UserDbContext` correcto a partir del claim
`db_name`.

No hay migraciones EF formales en el estado actual. La aplicacion usa
`EnsureCreated` y algunos `ExecuteSqlRaw` de compatibilidad en `Program.cs`.

## Ejecucion Local

Requisitos:

- .NET SDK 8.
- PostgreSQL accesible.
- PowerShell o shell equivalente.
- Opcional: Docker y Docker Compose.

La solucion principal esta en `Server/Server.sln` e incluye `Server` y
`Client`.

### Opcion 1: PostgreSQL local en el puerto 5432

El `Server/appsettings.json` de desarrollo espera:

```json
{
  "ConnectionStrings": {
    "Core": "Host=localhost;Port=5432;Database=pixelagents_core;Username=postgres;Password=postgres",
    "TenantTemplate": "Host=localhost;Port=5432;Database={db};Username=postgres;Password=postgres"
  }
}
```

Con esa configuracion:

```powershell
dotnet restore .\Server\Server.sln
dotnet build .\Server\Server.sln
dotnet run --project .\Server\Server.csproj
```

En otra terminal:

```powershell
dotnet run --project .\Client\Client.csproj
```

El servidor escucha en `http://localhost:5000`. El cliente de desarrollo lee
`Client/wwwroot/appsettings.json`, donde `ApiBaseUrl` apunta a ese puerto.

### Opcion 2: PostgreSQL desde Docker Compose

`docker-compose.yml` expone PostgreSQL en `127.0.0.1:${PG_PORT:-5433}`. Si solo
levantas Postgres con Compose y ejecutas el servidor fuera de Docker, debes
sobrescribir las cadenas de conexion porque el appsettings local usa 5432.

```powershell
docker compose up -d postgres

$env:ConnectionStrings__Core = "Host=localhost;Port=5433;Database=pixelagents_core;Username=postgres;Password=changeme_secure_password"
$env:ConnectionStrings__TenantTemplate = "Host=localhost;Port=5433;Database={db};Username=postgres;Password=changeme_secure_password"

dotnet run --project .\Server\Server.csproj
```

Despues ejecuta el cliente:

```powershell
dotnet run --project .\Client\Client.csproj
```

### Configuracion local frecuente

Variables y claves relevantes:

- `ConnectionStrings__Core`: base global.
- `ConnectionStrings__TenantTemplate`: plantilla para bases tenant; debe incluir
  `{db}`.
- `AllowedOrigin`: origen permitido por CORS en produccion.
- `BaseUrl` o `Telegram:WebhookBaseUrl`: URL publica para webhooks y enlaces
  publicos cuando aplique.
- `WhatsApp:WebhookVerifyToken`: token de verificacion para webhook de WhatsApp.

Las API keys de proveedores no se configuran como variables globales en el flujo
normal. Se guardan desde la pantalla `/apikeys` y se asocian a modulos.

## Despliegue Con Docker

El despliegue incluido usa tres servicios:

- `postgres`: PostgreSQL 16 Alpine.
- `pixelagents`: imagen construida desde este repo.
- `dozzle`: visor web de logs de contenedores.

Comando basico:

```powershell
docker compose up -d --build
```

Variables de entorno soportadas por `docker-compose.yml`:

- `POSTGRES_PASSWORD`: password de PostgreSQL.
- `PG_PORT`: puerto local de PostgreSQL, por defecto `5433`.
- `APP_PORT`: puerto local de la aplicacion, por defecto `8080`.
- `PUBLIC_IP`: usado para construir `AllowedOrigin`.
- `DOZZLE_PORT`: puerto local de Dozzle, por defecto `9999`.
- `DOZZLE_USERNAME`, `DOZZLE_PASSWORD`, `DOZZLE_KEY`: credenciales de Dozzle.
- `GIT_COMMIT`: commit inyectado en el build.

`Dockerfile` tiene tres etapas:

1. `build-server`: restaura y publica `Server/Server.csproj`.
2. `build-client`: restaura y publica `Client/Client.csproj`.
3. `runtime`: usa `mcr.microsoft.com/dotnet/aspnet:8.0`, instala nginx, copia
   servidor y cliente, y arranca con `entrypoint.sh`.

En runtime:

- nginx escucha en el puerto 80.
- Blazor se sirve desde `/var/www/html`.
- la API .NET corre en `http://0.0.0.0:5000`.
- nginx proxya `/api/`, `/hubs/` y `/swagger` al server .NET.
- el volumen `media` persiste `/app/GeneratedMedia`.

`deploy.sh` hace:

```bash
git pull origin main
export GIT_COMMIT=$(git rev-parse --short HEAD)
docker compose build --no-cache
docker compose up -d
```

El endpoint `/api/build-info` lee `build-info.json` generado durante el publish
del servidor y devuelve commit y fecha de build cuando existen.

## Pipeline De IA

El pipeline actual se ejecuta como grafo de dependencias. El executor activo es
`GraphPipelineExecutor`, registrado como implementacion de `IPipelineExecutor`.
El executor legacy `PipelineExecutor` fue retirado del arbol de codigo; la ruta
activa es el grafo.

Conceptos:

- Un `Project` contiene `ProjectModules`.
- Cada `ProjectModule` instancia un `AiModule`.
- `ModuleConnection` conecta un puerto de salida con un puerto de entrada.
- `ModulePortRegistry` define los puertos visibles del cliente.
- `ExecutionGraph` construye el grafo runtime.
- `ModuleNode` representa estado, entradas, salidas y output de cada nodo.
- `PortDataResolver` transforma `StepOutput` en datos por puerto.
- `PausedGraphState` serializa estado para pausa/reanudacion.
- Cada tipo de modulo se ejecuta mediante un `IModuleHandler`.

Estados de nodo:

- `Pending`.
- `Ready`.
- `Running`.
- `Completed`.
- `Failed`.
- `Paused`.
- `Skipped`.

El flujo general de ejecucion:

1. El executor carga proyecto, modulos activos y conexiones.
2. Valida que exista exactamente un modulo `Start`; ese modulo es el unico
   punto de entrada.
3. Crea un `ProjectExecution` y un workspace bajo `GeneratedMedia`.
4. Construye `ExecutionGraph`.
5. Marca el modulo `Start` como `Ready` y lanza cualquier nodo en ese estado.
6. Cuando un handler termina, persiste `StepExecution`, archivos y logs.
7. `CompleteNodeAndPrepareDownstream` propaga outputs a puertos downstream y
   solo marca como `Ready` los modulos cuyos inputs estan satisfechos.
8. Continua el bucle de nodos `Ready` hasta completar, fallar, cancelar o
   pausar.

`GraphPipelineExecutor` tambien implementa:

- reintentos por modulo: `RetryFromModuleAsync`.
- reanudacion de interacciones: `ResumeFromInteractionAsync`.
- aprobacion/rechazo de orquestador: `ResumeFromOrchestratorAsync`.
- aprobacion/rechazo de checkpoint: `ResumeFromCheckpointAsync`.
- aborto de interaccion: `AbortFromInteractionAsync`.
- cola de interacciones: `SendNextQueuedInteractionAsync` y
  `CancelQueuedInteractionsAsync`.

### Modulos Soportados

Handlers actuales:

- `Start`: emite el prompt de entrada del usuario.
- `StaticText`: emite texto fijo configurado.
- `FileUpload`: expone archivos adjuntos como recurso de pipeline.
- `Text`: generacion de texto mediante proveedor IA.
- `Image`: generacion o edicion de imagenes.
- `Video`: generacion de video.
- `VideoEdit`: composicion/render de video.
- `VideoSearch`: busqueda de video en Pexels.
- `Audio`: texto a voz.
- `Transcription`: audio a texto.
- `Embeddings`: generacion de embeddings.
- `Orchestrator`: planificacion y salidas multiples.
- `Coordinator`: agregacion de multiples entradas y llamada IA.
- `Scene`: construccion de JSON de escena.
- `Checkpoint`: pausa para revision humana.
- `Interaction`: pausa y espera respuesta por canal externo.
- `Design`: creacion de disenos con Canva.
- `Publish`: publicacion social via Buffer.

Modulos de sistema creados por defecto en `SystemModuleCatalog`:

- `FileUpload`.
- `StaticText`.
- `Checkpoint`.

El cliente tambien conoce otros tipos mediante `ModulePortRegistry`, incluyendo
`Start`, `Scene`, `Interaction`, `Coordinator`, `Orchestrator`, `Publish` y los
tipos IA.

### Providers IA

Todos los proveedores implementan `IAiProvider`:

```csharp
string ProviderType { get; }
IEnumerable<string> SupportedModuleTypes { get; }
Task<AiResult> ExecuteAsync(AiExecutionContext context);
Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey);
```

Providers registrados:

- `OpenAiProvider`.
- `AnthropicProvider`.
- `GeminiProvider`.
- `GrokProvider`.
- `LeonardoProvider`.
- `PexelsProvider`.
- `Json2VideoProvider`.

`IAiProviderRegistry` resuelve proveedores por `ProviderType` y expone la lista
de proveedores disponibles.

### Handlers De Modulo

Todos los handlers implementan `IModuleHandler`:

```csharp
string ModuleType { get; }
Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx);
```

`ModuleExecutionContext` entrega al handler:

- nodo runtime.
- grafo.
- proyecto y ejecucion.
- tenant.
- workspace.
- reglas obligatorias.
- inputs ya resueltos por puerto.
- configuracion fusionada de modulo.
- archivos adjuntos.
- rutas de media.
- helpers para leer texto, archivos y config.

El handler devuelve `ModuleResult` con estado `Completed`, `Failed` o `Paused`,
output estructurado, coste y archivos producidos.

## API HTTP Y SignalR

La API esta definida en `Server/Program.cs`. Casi todos los endpoints de gestion
de tenant requieren autenticacion por cookie.

### Autenticacion

- `POST /api/auth/register`: crea usuario, cuenta y tenant.
- `POST /api/auth/login`: inicia sesion con cookie.
- `POST /api/auth/logout`: cierra sesion.
- `GET /api/auth/me`: devuelve usuario autenticado y tenant.

### API Keys

Gestion de claves por tenant:

- crear, listar, obtener, actualizar y eliminar en `/api/apikeys`.
- las respuestas no exponen el secreto completo; el flujo normal usa las claves
  desde modulos.

### Reglas

Gestion de reglas obligatorias:

- listar, crear, actualizar y eliminar en `/api/rules`.
- las reglas activas se cargan como contexto obligatorio durante la ejecucion del
  grafo.

### Modulos IA Y Archivos De Modulo

Gestion de catalogo reusable:

- `/api/modules`: crear, listar, obtener, actualizar y eliminar modulos.
- `/api/modules/{moduleId}/files`: subir y listar archivos asociados a un
  modulo.
- `/api/module-files`: listar todos los archivos del tenant.
- `/api/module-files/{fileId}/download`: descargar archivo de modulo.
- `/api/module-files/{fileId}`: eliminar archivo.

Si se solicita crear un modulo de sistema conocido, `SystemModuleCatalog`
garantiza o actualiza su definicion.

### Proyectos Y Grafo

Gestion de pipelines:

- `/api/projects`: crear y listar proyectos.
- `/api/projects/{id}`: obtener detalle, actualizar o eliminar.
- `/api/projects/{id}/duplicate`: duplicar proyecto completo.
- `/api/projects/{id}/graph`: guardar layout bruto.
- `/api/projects/{projectId}/graph/save`: guardar posiciones, conexiones,
  conteos de escenas y configs de modulos.
- `/api/projects/{projectId}/modules`: agregar modulos al proyecto.
- `/api/projects/{projectId}/modules/{id}`: actualizar o eliminar instancia.

El orden de ejecucion no se guarda en `ProjectModule`: lo determina el modulo
`Start` y las conexiones `OutgoingConnections`/`IncomingConnections`.

### Ejecuciones

Ejecucion y revision:

- `/api/projects/{projectId}/execute`: inicia ejecucion.
- `/api/projects/{projectId}/executions`: lista ejecuciones del proyecto.
- `/api/executions/{id}`: detalle de ejecucion.
- `/api/executions/{executionId}/logs`: logs persistidos.
- `/api/executions/{executionId}/retry-from-module`: retry desde un modulo del
  grafo.
- `/api/executions/{executionId}/orchestrator-review`: aprobar/rechazar
  orquestador.
- `/api/executions/{executionId}/checkpoint-review`: aprobar/rechazar
  checkpoint.
- `/api/projects/{projectId}/cancel`: cancelar ejecucion activa.

### Salidas De Orquestador

Gestion de outputs configurables para un modulo `Orchestrator`:

- listar en `/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs`.
- crear, actualizar y eliminar outputs bajo la misma ruta.

### Archivos De Ejecucion

Descarga privada y publica:

- `/api/executions/{executionId}/files/{fileId}`: descarga autenticada.
- `/api/public/files/{tenant}/{executionId}/{fileId}/{fileName}`: URL publica
  usada por servicios externos cuando necesitan acceder a un archivo producido.

La resolucion de rutas contempla workspaces relativos actuales, rutas absolutas
legacy y re-rooting bajo `GeneratedMedia`.

### Programaciones

Cada proyecto puede tener una programacion:

- `GET /api/projects/{projectId}/schedule`.
- `POST /api/projects/{projectId}/schedule`.
- `PUT /api/projects/{projectId}/schedule`.
- `DELETE /api/projects/{projectId}/schedule`.

`SchedulerBackgroundService` calcula proximas ejecuciones con Cronos y ejecuta
proyectos habilitados.

### Integraciones De Mensajeria Y Publicacion

Configuracion por proyecto:

- WhatsApp: `/api/projects/{projectId}/whatsapp-config`.
- Telegram: `/api/projects/{projectId}/telegram-config`.
- Diagnostico Telegram: `/api/projects/{projectId}/telegram-webhook-info`.
- Instagram via Buffer: `/api/projects/{projectId}/instagram-config`.
- TikTok via Buffer: `/api/projects/{projectId}/tiktok-config`.

Webhooks:

- `GET /api/webhooks/whatsapp`: verificacion del webhook.
- `POST /api/webhooks/whatsapp`: recepcion de mensajes y reanudacion de
  interacciones.
- `POST /api/webhooks/telegram`: recepcion de updates Telegram.

Si Telegram no tiene `WebhookBaseUrl`, el servicio intenta trabajar en modo
polling mediante `TelegramPollingService`.

### Build Info

- `GET /api/build-info`: devuelve commit y fecha de build si existe
  `build-info.json`; si no, devuelve valores `unknown`.

### SignalR

Hub:

- `/hubs/execution`.

Metodos cliente-servidor:

- `JoinProject(projectId)`.
- `LeaveProject(projectId)`.

Eventos emitidos por servidor:

- `ExecutionLog`: log de ejecucion.
- `StepProgress`: estado de modulo.
- `OrchestratorTaskProgress`: progreso detallado de tareas del orquestador.

`SignalRExecutionLogger` emite en tiempo real y, si tiene `UserDbContext`,
persiste tambien en `ExecutionLogs`.

## Integraciones Externas

### OpenAI, Anthropic, Gemini Y Grok

Usados como proveedores de generacion de texto y capacidades IA segun el tipo de
modulo. Las claves se guardan en `/apikeys` y se asocian a modulos en
`/modules`.

### Leonardo

Proveedor de imagen. Puede producir archivos persistidos como `ExecutionFile`
en el workspace de la ejecucion.

### Pexels

Proveedor de busqueda de video para modulos `VideoSearch`.

### Json2Video

Proveedor de video/render. Se usa especialmente desde `Video` o `VideoEdit`,
incluyendo modos con escenas o variables de plantilla segun configuracion.

### Canva

`CanvaService` permite crear disenos, ejecutar autofill, exportar, subir assets
y consultar datasets/listas de disenos. Lo usan modulos `Design` y flujos de
publicacion cuando aplica.

### Buffer, Instagram Y TikTok

El archivo `Server/Services/Instagram/MetricoolService.cs` contiene clases
`BufferService`, `BufferConfig`, `BufferChannel` y resultados de publicacion. El
servicio publica contenido en canales configurados para Instagram/TikTok.

### WhatsApp

`WhatsAppService` envia texto, sube media y envia imagenes/videos. Las
respuestas entrantes se correlacionan con `WhatsAppCorrelation` para reanudar
pipelines pausados.

### Telegram

`TelegramService` envia mensajes, opciones inline, fotos y videos, y gestiona
webhook. `TelegramUpdateHandler` procesa updates y
`TelegramPollingService` cubre el modo polling.

## Estructura De Archivos

```text
.
|-- Client/
|   |-- Components/Pipeline/
|   |-- Layout/
|   |-- Models/
|   |-- Pages/
|   |-- Services/
|   `-- wwwroot/
|-- Data/
|-- Server/
|   |-- Data/
|   |-- Hubs/
|   |-- Models/
|   |-- Services/
|   |   |-- Ai/
|   |   |   `-- Handlers/
|   |   |-- Canva/
|   |   |-- Instagram/
|   |   |-- Scheduler/
|   |   |-- Telegram/
|   |   `-- WhatsApp/
|   |-- GeneratedMedia/     runtime
|   `-- storage/            runtime
|-- docs/
|-- docker-compose.yml
|-- Dockerfile
|-- nginx.conf
|-- deploy.sh
`-- entrypoint.sh
```

## Documentacion Existente

`docs/MIGRATION_PLAN_GRAPH_EXECUTOR.md` y `docs/MIGRATION_PROGRESS.md`
documentan la migracion hacia el executor basado en grafos. Son utiles como
contexto historico, pero no deben leerse como unica fuente de verdad.

Estado actual relevante:

- `GraphPipelineExecutor` ya esta registrado como `IPipelineExecutor`.
- `ProjectModule` no conserva orden ni ramas; la ejecucion depende de
  `ModuleConnection`.
- `StepExecution` no conserva orden; se vincula a `ProjectModuleId`.

- No se detectan proyectos de tests en el repo.

## Comandos Utiles

Restaurar y compilar:

```powershell
dotnet restore .\Server\Server.sln
dotnet build .\Server\Server.sln
```

Ejecutar servidor:

```powershell
dotnet run --project .\Server\Server.csproj
```

Ejecutar cliente:

```powershell
dotnet run --project .\Client\Client.csproj
```

Levantar stack Docker:

```powershell
docker compose up -d --build
```

Ver estado de contenedores:

```powershell
docker compose ps
```

Ver logs:

```powershell
docker compose logs -f pixelagents
```

## Notas De Mantenimiento

- Mantener `Client/Models/Dtos.cs` y `Server/Models/Dtos.cs` sincronizados
  cuando cambien contratos HTTP.
- Si se agregan tipos de modulo, actualizar servidor, handler, DI,
  `ModulePortRegistry`, UI de configuracion y documentacion.
- Si se agregan proveedores, implementar `IAiProvider`, registrarlo en DI y
  documentar `ProviderType` y tipos soportados.
- Si se cambian rutas HTTP, actualizar `ApiClient` y esta guia.
- Evitar guardar secretos reales en archivos versionados. Usar placeholders,
  variables de entorno o la pantalla de API keys.
- No versionar salidas de `GeneratedMedia`, `storage`, `bin` u `obj`.
- El modelo de migraciones actual usa `EnsureCreated` y SQL manual; cualquier
  cambio fuerte de esquema deberia planear compatibilidad con bases tenant ya
  existentes.
