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
5. El usuario crea pipelines y conecta modulos en un canvas visual; puede
   agruparlos en proyectos para organizarlos.
6. El servidor ejecuta el grafo con `GraphPipelineExecutor`.
7. Los logs, progreso y estados llegan al cliente en tiempo real por SignalR.
8. Los archivos producidos quedan asociados a ejecuciones y pueden descargarse.

Entidades funcionales principales:

- `Account` y `ApplicationUser`: identidad, cuenta y tenancy.
- `ApiKey`: credenciales por proveedor, guardadas por tenant.
- `AiModule`: definicion reutilizable de un modulo.
- `ProjectGroup`: proyecto de alto nivel; agrupa pipelines de forma puramente
  organizativa (titulo y descripcion, sin efecto en la ejecucion).
- `Project`: pipeline editable; pertenece como mucho a un `ProjectGroup`.
  Borrarlo es un borrado logico (`DeletedAt`): pasa a la papelera y se conserva
  entero hasta que el usuario lo restaure o lo elimine definitivamente.
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
- `/modules`: catalogo y configuracion de modulos.
- `/biblioteca`: archivos subidos a modulos.
- `/projects`: listado de proyectos y pipelines. Cada proyecto es una agrupacion
  plegable (titulo, descripcion y sus pipelines dentro) a la que se pueden anadir
  pipelines nuevos o ya existentes; los que no pertenecen a ninguno se listan al
  final en "Sin proyecto" y "Pipelines de prueba". Dentro de cada seccion, los
  pipelines fijados se muestran primero.
- `/projects/{ProjectId:guid}`: detalle, editor visual, ejecuciones e
  integraciones del pipeline.
- `/papelera`: pipelines borrados. Se pueden restaurar o eliminar
  definitivamente; no caducan solos.
- `/configuracion/{seccion?}`: ajustes del tenant en una sola pagina, con una
  pestana por seccion: `apikeys` (claves de proveedor), `redes-sociales`,
  `mensajeria`, `shopify` y `reglas` (reglas obligatorias, con sub-pestanas
  "Mis reglas" y "Constantes"). Cada seccion es un componente de
  `Client/Components/Settings/` y solo se monta la activa.

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
- `IAiProvider` para OpenAI, Anthropic, Leonardo, Gemini y Grok.
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
  variables de proyecto, conexiones, ejecuciones, logs, archivos, schedules,
  salidas de orquestador y reglas.

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

#### Archivo .env

Para producción con Docker Compose, se recomienda usar un archivo `.env` en la
raíz del proyecto. Copia `.env.example` a `.env` y configura las variables:

```bash
cp .env.example .env
```

Variables y claves relevantes:

- `PUBLIC_IP`: **CRÍTICO** - IP pública o dominio del servidor. Necesario para
  que Buffer y otros servicios externos puedan acceder a archivos generados.
  Obtén tu IP con `curl ifconfig.me`.
- `APP_PORT`: puerto de la aplicación en el host (por defecto 8080).
- `POSTGRES_PASSWORD`: contraseña de PostgreSQL.
- `ConnectionStrings__Core`: base global.
- `ConnectionStrings__TenantTemplate`: plantilla para bases tenant; debe incluir
  `{db}`.
- `AllowedOrigin`: origen permitido por CORS en produccion.
- `BaseUrl`: URL pública completa (ej: `http://123.45.67.89:8080`). Se construye
  automáticamente desde `PUBLIC_IP` y `APP_PORT`. **Esencial para publicación
  en redes sociales con Buffer**.
- `Telegram:WebhookBaseUrl`: URL publica para webhooks de Telegram.
- `WhatsApp:WebhookVerifyToken`: token de verificacion para webhook de WhatsApp.

Las API keys de proveedores no se configuran como variables globales en el flujo
normal. Se guardan en Configuracion (`/configuracion/apikeys`) y se asocian a
modulos.

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
- `ExecutionVariables` resuelve las variables del pipeline (`tematica`,
  `keyword`...): sustituye `{{clave}}` en el prompt inicial y en la config de
  cada modulo, e inyecta los valores como bloque en el system prompt de todas
  las llamadas a IA.
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
3. Crea un `ProjectExecution` y un workspace bajo `GeneratedMedia`, y fija en el
   las variables con las que corre (`VariablesJson`), para que reintentos y
   reanudaciones usen los mismos valores.
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
- `FileDirectory`: publica un directorio de ficheros en carpetas y subcarpetas y
  emite su indice (descripcion y URL de descarga de cada fichero), no los
  ficheros en si. Las carpetas y las subidas se gestionan desde el explorador
  del nodo, disponible tanto en el inspector como en "Editar nodo"
  (ver `docs/architecture.md`).
- `Text`: generacion de texto mediante proveedor IA.
- `Image`: generacion o edicion de imagenes.
- `Video`: anima imagenes (imagen -> video). Genera un clip por imagen de
  entrada, con las llamadas en paralelo (ver `docs/architecture.md`).
- `VideoAssembly`: une varios clips en un unico MP4 con ffmpeg.
- `Audio`: texto a voz.
- `Transcription`: audio a texto.
- `Embeddings`: generacion de embeddings.
- `Orchestrator`: planificacion y salidas multiples.
- `Coordinator`: agregacion de multiples entradas y llamada IA.
- `Scene`: construccion de JSON de escena.
- `Checkpoint`: pausa para revision humana.
- `Conditional`: evalua una condicion escrita y decide por que rama sigue el
  pipeline (ver `docs/architecture.md`).
- `Interaction`: pausa y espera respuesta por canal externo.
- `Design`: creacion de disenos con Canva.
- `Publish`: publicacion social via Buffer. La red de destino se elige por nodo
  en el inspector (config `provider`) y el nodo del grafo muestra su logotipo.

Modulos de sistema creados por defecto en `SystemModuleCatalog`:

- `FileUpload`.
- `StaticText`.
- `FileDirectory`.
- `Checkpoint`.
- `Conditional`.
- `Publish`.
- `SubProject`.

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
- son reglas **propias del tenant** y viven en su BD. Aparte estan las reglas
  **integradas** (comportamiento, formato ASCII y veto de marcas), que no estan
  en `/api/rules`: son constantes de `Server/Services/Ai/OutputSchema.cs`
  (`GetTextContentRules`) que el servidor antepone en los modulos de texto,
  coordinador y orquestador. La pestana `reglas` de `/configuracion` se divide en
  dos sub-pestanas, **Mis reglas** (las propias, editables) y **Constantes** (las
  integradas, solo lectura), con el mismo texto que ensena el inspector del
  pipeline (`ActiveRulesRegistry.BuiltInTextRules`, en
  `Client/Models/PipelineGraphModels.cs`).

### Modulos IA Y Archivos De Modulo

Gestion de catalogo reusable:

- `/api/modules`: crear, listar, obtener, actualizar y eliminar modulos.
- `/api/modules/{moduleId}/files`: subir y listar archivos asociados a un
  modulo.
- `/api/module-files`: listar todos los archivos del tenant.
- `/api/module-files/{fileId}/download`: descargar archivo de modulo.
- `/api/module-files/{fileId}`: eliminar archivo.

Directorio de archivos (publico, sin autenticacion, para que el destinatario del
indice pueda descargar):

- `/api/public/directory/{tenant}/{moduleId}`: indice resuelto del directorio.
- `/api/public/directory/{tenant}/{moduleId}/{ruta}`: fichero declarado en el indice.

El nodo Directorio (la "biblioteca" del pipeline) puede limitar cada ejecucion a
una carpeta con su ajuste `folder`: vacio publica todo, una ruta publica esa
carpeta y sus subcarpetas, y el marcador de una variable (`{{carpeta}}`) deja
elegir los documentos al lanzar cada ejecucion. Si el ajuste esta vacio pero el
pipeline tiene variables de tipo `folder`, el nodo entrega la carpeta que esas
variables traigan en cada ejecucion, sin necesidad de escribir nada en el. Si la
variable se queda sin carpeta o la carpeta no existe, el modulo falla en vez de
publicar el directorio entero.

Si se solicita crear un modulo de sistema conocido, `SystemModuleCatalog`
garantiza o actualiza su definicion.

### Proyectos Y Grafo

Agrupacion de pipelines en proyectos (organizativa: borrar un proyecto no borra sus
pipelines):

- `GET /api/project-groups`: lista los proyectos con su numero de pipelines.
- `POST /api/project-groups`: crear proyecto (titulo y descripcion).
- `PUT /api/project-groups/{id}`: renombrar o cambiar la descripcion.
- `DELETE /api/project-groups/{id}`: eliminar el proyecto; sus pipelines pasan a
  quedar sin agrupar.
- `POST /api/project-groups/{id}/projects`: anadir pipelines existentes al proyecto
  (si estaban en otro, se mueven).

Gestion de pipelines:

- `/api/projects`: crear y listar pipelines.
- `/api/projects/{id}`: obtener detalle o actualizar. `DELETE` mueve el pipeline
  a la papelera (borrado logico); no lo elimina.
- `/api/projects/{id}/duplicate`: duplicar pipeline completo (la copia se queda en
  el mismo proyecto que el original).
- `PUT /api/projects/{id}/pin`: fijar o desfijar el pipeline en el listado.
- `PUT /api/projects/{id}/group`: mover el pipeline a otro proyecto; `null` lo deja
  sin agrupar.

Papelera de pipelines:

- `GET /api/projects/trash`: pipelines borrados, con la fecha de borrado y cuantos
  modulos y ejecuciones se perderian.
- `POST /api/projects/{id}/restore`: devolverlo al listado (recalcula la proxima
  ejecucion programada si tenia programacion activa).
- `DELETE /api/projects/{id}/permanent`: borrado definitivo; solo funciona sobre
  pipelines que ya estan en la papelera.

Un pipeline en la papelera no se lista, no se abre, no se ejecuta (ni a mano, ni
programado, ni como sub-proyecto de otro pipeline) y no cuenta en los usos de
modulos ni de conexiones. Sus archivos tampoco salen en la biblioteca.
- `/api/projects/{id}/graph`: guardar layout bruto.
- `/api/projects/{projectId}/graph/save`: guardar posiciones, conexiones,
  conteos de escenas y configs de modulos.
- `/api/projects/{projectId}/modules`: agregar modulos al proyecto.
- `/api/projects/{projectId}/modules/{id}`: actualizar o eliminar instancia.

El orden de ejecucion no se guarda en `ProjectModule`: lo determina el modulo
`Start` y las conexiones `OutgoingConnections`/`IncomingConnections`.

### Variables Del Pipeline

Datos que el pipeline declara una vez con nombre y descripcion (`tematica`,
`keyword`...) y cuyo valor se elige en cada ejecucion; nunca llevan valor fijo. Se sustituyen en los prompts (`{{clave}}`) y se
inyectan como bloque en el system prompt de todos los modulos; ver
`docs/architecture.md` > "Variables de ejecucion".

- `GET /api/projects/{projectId}/variables`: lista las variables del proyecto. Cada
  una lleva su tipo: `text` (valor escrito a mano) o `folder`, que en vez de
  descripcion declara de que biblioteca del pipeline se elige la carpeta. El valor,
  como el de cualquier variable, lo pone la ejecucion o la planificacion.
- `GET /api/projects/{projectId}/directory-folders`: carpetas de cada nodo Directorio,
  que son las opciones de las variables de tipo `folder`.
- `POST /api/projects/{projectId}/variables`: crea una variable.
- `PUT /api/projects/{projectId}/variables/{variableId}`: actualiza nombre o
  descripcion.
- `DELETE /api/projects/{projectId}/variables/{variableId}`: elimina una variable.

En una ejecucion manual el prompt y el valor de cada variable se rellenan por
separado en el panel de ejecucion. En una planificacion automatica es el
planificador el que, ademas del prompt, propone el valor de cada variable para
cada ejecucion futura (`PlannedPrompts.VariablesJson`), corregible a mano desde
la cola. Una ejecucion planificada tambien se puede escribir entera a mano en la
cola, con la IA redactando el prompt y las variables si se le pide.

### Ejecuciones

Ejecucion y revision:

- `/api/projects/{projectId}/execute`: inicia ejecucion; acepta el valor de las
  variables del pipeline para esa corrida.
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
- `GET /api/projects/{projectId}/schedule/upcoming`: proyecta las proximas
  ejecuciones programadas (timeline del planificador).
- `POST /api/projects/{projectId}/schedule`.
- `PUT /api/projects/{projectId}/schedule`.
- `DELETE /api/projects/{projectId}/schedule`.

`SchedulerBackgroundService` calcula proximas ejecuciones con Cronos y ejecuta
proyectos habilitados. La programacion tambien guarda el valor de las variables
del pipeline que usara en cada corrida; cuando consume la cola del planificador,
los valores que trae el prompt planificado mandan sobre los de la programacion.

### Cola Del Planificador

Las ejecuciones planificadas de un proyecto (`PlannedPrompt`): el prompt y el valor
de las variables con que correra cada una. Se llenan en lote con el generador o
**una a una a mano**, con la IA como ayuda opcional para redactarlas.

- `GET /api/projects/{projectId}/planned-prompts`: lista la cola.
- `POST /api/projects/{projectId}/planned-prompts/generate`: genera N ejecuciones
  con el planificador.
- `POST /api/projects/{projectId}/planned-prompts/draft`: redacta **una** ejecucion
  (prompt + variables) a partir de la idea del usuario y del borrador que lleve
  escrito. No guarda nada: la propuesta vuelve al formulario para revisarla.
- `POST /api/projects/{projectId}/planned-prompts`: anade una ejecucion a la cola.
- `PUT|DELETE /api/projects/{projectId}/planned-prompts/{promptId}`: edita o quita
  una ejecucion pendiente.
- `POST /api/projects/{projectId}/planned-prompts/reorder`: reordena la cola.
- `POST /api/projects/{projectId}/planned-prompts/{promptId}/execute`: lanza esa
  ejecucion ahora, sin esperar a la programacion.

### Integraciones De Mensajeria Y Publicacion

Las credenciales se definen una sola vez como **conexiones reutilizables** y
luego cada proyecto solo asigna cual usar (no se reintroducen por proyecto):

- Redes sociales (Buffer): `GET|POST /api/social-connections`,
  `PUT|DELETE /api/social-connections/{id}`. Cada conexion guarda token de Buffer
  + canal para una plataforma (`instagram`, `tiktok` o `pinterest`).
- Mensajeria (Telegram): `GET|POST /api/messaging-connections`,
  `PUT|DELETE /api/messaging-connections/{id}`. Cada conexion guarda el bot token
  + chat id.
- Shopify: `GET|POST /api/shopify-connections`,
  `PUT|DELETE /api/shopify-connections/{id}`. Cada conexion guarda el dominio de la
  tienda + Client ID + Client Secret de una app del Dev Dashboard; el access token se
  obtiene por client credentials grant (caduca cada 24 h, se renueva solo). El modulo
  `ShopifyBlog` publica articulos de blog; el blog destino se elige en cada nodo
  (`GET /api/projects/{projectId}/shopify/blogs`). Ademas del titulo y el cuerpo,
  el nodo permite configurar extracto, identificador URL (slug), titulo de pagina
  y metadescripcion SEO; si se dejan vacios se generan a partir del titulo/contenido.
  El extracto y el slug son campos nativos del articulo, y el SEO se guarda como
  metafields `global.title_tag` / `global.description_tag` (requiere scope `write_content`).
  El nodo tiene ademas un puerto de entrada opcional `input_image` (imagen destacada):
  si conectas un modulo de Imagen, el articulo se publica con esa portada usando el
  campo nativo `image` del articulo (Shopify descarga la URL publica del archivo y la
  re-hostea en su CDN al crear el articulo). El texto alternativo sale del nodo, del
  JSON (`imagen_alt`) o, en su defecto, del titulo. Si no conectas nada, se publica sin
  imagen como antes.
  El modulo de IA anterior puede emitir el articulo ya estructurado en un unico JSON
  usando el "Formato de la conexion" del cable (hay una plantilla predefinida de
  Shopify con `titulo`, `cuerpo`, `extracto`, `slug`, `seo_titulo`, `seo_descripcion`,
  `imagen_alt`, `tags`); el nodo parsea ese JSON y reparte cada campo. Precedencia:
  config del nodo > JSON del modulo anterior > autogenerado. Si la salida no es JSON,
  todo el texto se usa como cuerpo (retrocompatible).
- Canales de Buffer disponibles para un token: `GET /api/buffer/channels?apiKey=...`.

Asignacion por proyecto:

- `GET|PUT /api/projects/{projectId}/connections`: asigna las conexiones de
  Instagram, TikTok, Pinterest, Telegram y Shopify que usa el proyecto (por Id).

En la UI esto vive en Configuracion, en las pestanas **Redes sociales**
(`/configuracion/redes-sociales`), **Mensajeria** (`/configuracion/mensajeria`) y
**Shopify** (`/configuracion/shopify`); dentro del proyecto, las pestanas de
configuracion solo muestran selectores de la conexion guardada.

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
modulo. Las claves se guardan en `/configuracion/apikeys` y se asocian a modulos
en `/modules`.

### Leonardo

Proveedor de imagen. Puede producir archivos persistidos como `ExecutionFile`
en el workspace de la ejecucion.

### Canva

`CanvaService` permite crear disenos, ejecutar autofill, exportar, subir assets
y consultar datasets/listas de disenos. Lo usan modulos `Design` y flujos de
publicacion cuando aplica.

### Buffer, Instagram Y TikTok

El archivo `Server/Services/Instagram/MetricoolService.cs` contiene clases
`BufferService`, `BufferConfig`, `BufferChannel` y resultados de publicacion. El
servicio publica contenido en canales configurados para Instagram/TikTok.

**IMPORTANTE**: Para que Buffer pueda descargar las imágenes y videos generados,
debes configurar la variable `PUBLIC_IP` con tu IP pública o dominio accesible
desde internet. Ver `docs/BUFFER_SETUP.md` para instrucciones detalladas.

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
|   |-- Components/Models/
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
