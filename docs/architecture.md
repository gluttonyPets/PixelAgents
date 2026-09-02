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
  |-- IAiProvider x5            (OpenAI, Anthropic, Google, xAI,
  |                               LeonardoAI)
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
`ApiKeys`, `AiModules`, `SocialConnections`, `MessagingConnections`, `ShopifyConnections`,
`Projects`, `ProjectModules`, `ModuleConnections`, `ProjectExecutions`, `StepExecutions`,
`ExecutionFiles`, `ExecutionLogs`, `ProjectSchedules`, `OrchestratorOutputs`, `Rules` y
`PromptVersions` (historial de versiones del prompt de cada modulo). Las
credenciales de redes sociales (Buffer), mensajeria (Telegram) y Shopify son conexiones
reutilizables que los proyectos referencian por Id.
No hay migraciones EF formales: la BD se crea con `EnsureCreated` y los cambios de
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
10. Si un nodo `Conditional` descarta una rama, sus modulos quedan en `Skipped` y el
    resto del grafo sigue con normalidad (ver "Modulo Condicional" mas abajo).
11. Al terminar todos los nodos, la ejecucion queda en `Completed`, `Failed` o `Cancelled`.

---

## Ejecuciones programadas y planificador

`SchedulerBackgroundService` revisa cada 30 s los `ProjectSchedules` vencidos y lanza el
pipeline. El input de cada corrida se decide asi:

- Si el schedule tiene `UsePromptQueue` (checkbox "usar planificador"), consume el siguiente
  `PlannedPrompt` pendiente de la cola del proyecto (por `OrderIndex`) y lo usa como input.
- Si la cola esta vacia, cae al `UserInput` estatico del schedule.

En la interfaz el planificador vive en su propio panel, que se abre con el boton
"Planificador" de la barra del canvas (junto a "Sub-proyecto"). Ese panel reune la
configuracion del generador, la cola de prompts, el historial y un timeline de
ejecuciones futuras: cada numero es el siguiente prompt pendiente y, si hay
programacion activa consumiendo la cola, la fecha estimada en que se lanzara. Las
fechas las proyecta `GET /api/projects/{projectId}/schedule/upcoming`, que encadena
`SchedulerBackgroundService.ComputeNextRun` sobre su propio resultado.

Cuando el planificador esta activo pero **no queda ningun prompt** (cola vacia y sin
`UserInput`), el scheduler no ejecuta el pipeline con un prompt vacio: crea una correlacion
Telegram en estado `awaiting_planning` (asociada al proyecto, sin ejecucion) y envia un mensaje
al chat del proyecto pidiendo una nueva planificacion. La respuesta del usuario se procesa en
`TelegramUpdateHandler`, que genera prompts con `PromptPlannerService` (o, si no hay API Key,
toma cada linea como un prompt) y los encola como `PlannedPrompt` para las proximas corridas.
Solo se abre una peticion por proyecto a la vez.

### Botones de control de la interaccion (Telegram)

Cuando un nodo `Interaction` pausa el pipeline ("Revisa el contenido y confirma."), el mensaje
enviado al chat incluye los botones **Continuar**, **Abortar**, **Reiniciar**, **Editar** y
**Siguiente ejecución** (`ControlOptions`). Al pulsar **Siguiente ejecución** (`next_execution`),
`TelegramUpdateHandler` cancela la ejecucion actual como "cancelado por usuario"
(`AbortFromInteractionAsync`, estado `Cancelled`) y lanza de inmediato la siguiente tematica:
consume el siguiente `PlannedPrompt` pendiente del proyecto y arranca una nueva ejecucion con el.
Si la cola esta vacia, reutiliza el flujo `awaiting_planning` para pedir una nueva planificacion.

---

## Editor de pipelines: deshacer

`PipelineCanvas` guarda una pila de acciones deshacibles (boton "Deshacer" en la
barra y Ctrl+Z / Cmd+Z, que el listener de `pipeline-editor.js` ignora mientras
se escribe en un campo). Cubre lo que se pierde por accidente: borrar un nodo, y
borrar o crear una conexion. Mover nodos queda fuera a proposito, porque cada
arrastre llenaria la pila y taparia lo que interesa recuperar.

El borrado de un nodo **no se manda al servidor de inmediato**. El nodo se oculta
del canvas (`_hiddenModuleIds`, que `VisibleModules` filtra al repintar) y la
llamada real se confirma pasados 12 segundos, o antes si el usuario hace
cualquier otra cosa que guarde el grafo, o al salir del editor. Mientras espera
no se ha destruido nada, asi que deshacer devuelve el nodo tal cual estaba, con
su configuracion, sus archivos y sus conexiones.

Ese aplazamiento no es un adorno: `DELETE /api/projects/{id}/modules/{moduleId}`
arrastra en cascada las conexiones, los archivos del nodo y sus StepExecutions.
Recrear el nodo despues daria otro id y perderia los archivos subidos, asi que un
"deshacer" que borrase primero y recrease despues estaria mintiendo.

Mientras el borrado espera tampoco se guarda el grafo, y se cancela el guardado
con retardo que hubiera en vuelo. Si se recarga la pagina en ese hueco, el nodo
sigue intacto con sus conexiones: es el fallo mas benigno posible para algo que
se acaba de borrar sin querer.

## Modulos soportados

| Tipo          | Handler                    | Descripcion breve                                          |
|---------------|----------------------------|------------------------------------------------------------|
| Start         | StartModuleHandler         | Punto de entrada; inyecta el input del usuario al grafo    |
| StaticText    | StaticTextModuleHandler    | Emite texto estatico configurado en el modulo              |
| FileUpload    | FileUploadModuleHandler    | Pasa archivos adjuntos al modulo como salida               |
| FileDirectory | FileDirectoryModuleHandler | Publica un directorio de ficheros y emite su indice (ver seccion propia) |
| Text          | TextModuleHandler          | Genera texto con un proveedor LLM                          |
| Image         | ImageModuleHandler         | Genera imagenes con un proveedor de imagen                 |
| Audio         | AudioModuleHandler         | Genera audio (TTS) con un proveedor                        |
| Transcription | TranscriptionModuleHandler | Transcribe audio a texto via proveedor                     |
| Embeddings    | EmbeddingsModuleHandler    | Genera embeddings de texto via proveedor                   |
| Scene         | SceneModuleHandler         | Agrupa campos estaticos y puertos en un objeto de escena   |
| Orchestrator  | OrchestratorModuleHandler  | Planifica salidas dinamicas y las enruta a nodos hijo      |
| Coordinator   | CoordinatorModuleHandler   | Combina y resume resultados de ramas anteriores            |
| Interaction   | InteractionModuleHandler   | Pausa el pipeline y espera respuesta humana (Telegram/WA)  |
| Checkpoint    | CheckpointModuleHandler    | Pausa para revision humana antes de continuar              |
| Conditional   | ConditionalModuleHandler   | Evalua una condicion escrita y elige por que rama continua |
| Design        | DesignModuleHandler        | Genera disenos via proveedor grafico (Canva, etc.)         |
| Publish       | PublishModuleHandler       | Publica contenido en Instagram, TikTok, Pinterest o Threads via Buffer API |
| ShopifyBlog   | ShopifyBlogModuleHandler   | Publica un articulo de blog en Shopify (titulo, cuerpo, extracto, slug, SEO e imagen destacada via `input_image`, que se sube a Shopify con `stagedUploadsCreate` y requiere el scope `write_files`). El cuerpo acepta HTML con CSS (inline o `<style>`): si el contenido contiene cualquier etiqueta HTML se envia intacto sin escapar; el texto plano se convierte en parrafos. Publica **visible** por defecto (desmarcar "Publicar" en el nodo lo deja como borrador). Devuelve en la salida y en `metadata` la URL del articulo en el admin (`adminUrl`, sirve para borradores) y la URL publica de la tienda (`publicUrl`) |

### Modulo Directorio de archivos: indice y URLs publicas

`FileDirectoryModuleHandler` publica un conjunto de ficheros organizados en
carpetas y subcarpetas. Es un modulo **solo de salida** (`CanStartWithoutInputs`,
como `StaticText` o `FileUpload`) con un unico puerto, `output_index`.

Lo que emite es el **indice**, no los ficheros. Un directorio grande no tiene que
arrastrar su peso por el pipeline: viaja la lista de ficheros con su descripcion
y su URL, y el modulo de destino descarga solo el que necesita.

El indice es obligatorio y se valida entero en `FileDirectoryIndex.Resolve`. Cada
entrada necesita tres cosas, y si falta una el modulo falla en vez de publicar un
directorio a medias:

- `path`: ruta dentro del directorio, con sus carpetas (`logos/primarios/logo.svg`).
- `description`: que es ese fichero. Sin esto el indice no cumple su funcion.
- una ruta accesible, que se resuelve en este orden: el fichero subido al nodo
  (por `fileId`, o por nombre en indices escritos a mano), la `url` absoluta de
  la entrada, o la `baseUrl` del directorio mas la ruta.

Un fichero subido manda sobre la `baseUrl` porque es el que el usuario coloco
ahi; el repositorio externo no tiene por que contenerlo. Las entradas se
identifican por `fileId` y no por nombre: es lo unico que distingue dos ficheros
llamados igual en carpetas distintas. Rutas duplicadas o con `..` se rechazan.

El indice tambien lleva `folders`, la lista de carpetas del directorio, para que
una carpeta recien creada no desaparezca por no tener ficheros todavia.

Configuracion, en el nodo (no en el modulo de catalogo, que es unico y comun a
todos los directorios): `index` (JSON), `baseUrl` (opcional) y `format`
(`markdown`, por defecto, o `json`).

El `index` se lee **solo** de la configuracion del nodo, no de la mezcla
catalogo + nodo que arma el executor. Un indice en el catalogo se aplicaria a
todos los directorios a la vez, con ids de ficheros subidos a otro nodo, y el
explorador (que siempre lee la del nodo) ensenaria una cosa mientras la ejecucion
usaria otra. Si el catalogo arrastra uno antiguo, se ignora y se avisa en el log.
`baseUrl` y `format` si se heredan, porque no referencian ficheros.

### Explorador del inspector

El indice no se escribe a mano: `Client/Components/FileDirectoryEditor.razor`
monta un explorador con el arbol de carpetas a la izquierda y el contenido a la
derecha, mas la URL base y el formato de salida. Permite crear carpetas y
subcarpetas, renombrarlas (arrastra las rutas de todo lo que cuelga), subir
ficheros a la carpeta seleccionada, describirlos, moverlos entre carpetas y
eliminarlos.

Aparece en dos sitios, ambos apuntando al mismo nodo, asi que se mantienen
sincronizados: la seccion "Directorio de archivos" del inspector y el popup
"Editar nodo". En el popup se guarda al momento y no depende de su boton
`Guardar cambios`, que escribe en el modulo de catalogo: el contenido del
directorio es del nodo, y el modulo de catalogo es unico y comun a todos los
directorios de todos los proyectos.

Todo lo que hace se serializa al mismo JSON que valida el servidor, y se guarda
en la configuracion del nodo por la via habitual del grafo (`SetModuleConfig` ->
`ModuleConfigEntry`). Las carpetas son virtuales: viven solo en el indice. Los
ficheros se suben al nodo con los endpoints ya existentes de
`/api/project-modules/{id}/files`.

Eliminar un fichero o una carpeta (con confirmacion en dos pasos) borra tambien
lo subido: fuera del indice no se sirve por la URL publica ni aparece en ninguna
otra pantalla, asi que conservarlo solo acumularia basura.

El explorador incluye una vista previa, "Indice que recibe el modulo", que pide
al servidor el indice ya resuelto y lista cada fichero con su URL como enlace.
Es la forma de comprobar que las rutas responden sin lanzar el pipeline: las URL
no se pueden construir en el cliente porque dependen del tenant y del dominio
publico. La sirve `GET /api/project-modules/{id}/directory-index`, que pide
sesion y, a diferencia del endpoint publico, devuelve tambien los errores de
validacion.

La biblioteca (`/biblioteca`) muestra ademas, por cada archivo, la URL con la
que este servidor lo expone, o "No expuesto" cuando no lo esta. Solo la tienen
los archivos de un nodo Directorio que su indice declara: un archivo subido pero
no indexado no se sirve. La calcula `FileDirectoryPublisher.BuildHostedUrlsAsync`
desde `/api/module-files`; el resto de endpoints dejan ese campo a null.

El directorio se expone sin autenticacion en:

- `GET /api/public/directory/{tenant}/{moduleId}`: el indice resuelto.
- `GET /api/public/directory/{tenant}/{moduleId}/{ruta}`: un fichero del indice.

El indice es la unica puerta de entrada: una ruta que no aparece en el no se
sirve, aunque el nodo tenga subido un fichero con ese nombre
(`FileDirectoryPublisher.FindHostedFile`).

### Modulo Condicional: ramas del pipeline

`ConditionalModuleHandler` es el unico modulo que decide **si el flujo continua**.
Tiene una entrada (`input`) y dos salidas: `output_true` ("Se cumple") y
`output_false` ("No se cumple"). La entrada se propaga tal cual por la rama viva,
asi que los modulos siguientes reciben lo mismo que si el condicional no
estuviera en medio; el nodo solo elige por donde sigue el grafo.

Configuracion del nodo (inspector del editor):

| Clave | Valores | Para que sirve |
|-------|---------|----------------|
| `condition` | texto libre | La condicion a evaluar. Obligatoria. |
| `conditionMode` | `auto` (defecto), `expression`, `ai` | Como se evalua. |
| `conditionProvider` / `conditionModel` | p. ej. `OpenAI` / `gpt-4o-mini` | Modelo del modo IA. Vacio = modelo por defecto del tenant (`AnalystDefaults`) segun las API Keys configuradas. |

Modos:

- `auto`: intenta primero la evaluacion determinista (`ConditionEvaluator`) y,
  si la condicion no encaja con esa gramatica, la delega en la IA.
- `expression`: solo determinista; si no se entiende, el modulo falla en vez de
  adivinar.
- `ai`: siempre pregunta al modelo, que responde `{"cumple": ..., "motivo": ...}`.

Gramatica del modo expresion (ignora mayusculas y acentos):

```
contiene "descuento"        no contiene "error"
empieza por "OK"            termina en "."
es igual a "aprobado"       distinto de "no"
esta vacio                  no esta vacio
longitud > 500              palabras >= 50
numero > 10                 coincide con /^[0-9]{4}$/
```

Los terminos se combinan con `y`/`and`/`&&` (todos) y `o`/`or`/`||` (alguno),
evaluados como disyuncion de conjunciones: `(A y B) o C`.

**Que pasa con la rama descartada.** El handler devuelve en
`ModuleResult.BlockedOutputPorts` el puerto que no se activa. El grafo marca esas
aristas como muertas (`PortConnection.IsDead`) y
`ExecutionGraph.SkipUnreachableNodes` deja en `Skipped` todos los modulos que
solo colgaban de ellas, en cascada. Detalles que importan:

- Un modulo alimentado ademas por una rama viva **no** se salta: se ejecuta con
  los datos de esa rama (las aristas muertas no cuentan para satisfacer puertos).
- Si `output_false` no esta conectado, no cumplirse la condicion equivale a
  detener ahi el pipeline: la ejecucion termina como **Completed**, no como
  fallida ni bloqueada.
- Cada modulo saltado deja su `StepExecution` en estado `Skipped`, para que la UI
  muestre que no se ejecuto y por que.
- Al reanudar (checkpoint/interaccion) o reintentar, el nodo condicional ya viene
  completo desde la BD: la rama viva se recupera del metadato `conditionMet` de
  su salida (`ConditionalBranching.ReadConditionMet`).
- Marcar "Saltar este paso" en un condicional lo neutraliza: deja pasar el flujo
  por la rama "se cumple".

---

## Proveedores IA

| Proveedor   | ProviderType | Modulos que sirve                              |
|-------------|--------------|------------------------------------------------|
| OpenAI      | `OpenAI`     | Text, Image (DALL-E)                           |
| Anthropic   | `Anthropic`  | Text (Claude)                                  |
| Google      | `Google`     | Text (Gemini), Image                           |
| xAI         | `xAI`        | Text (Grok), Image                             |
| LeonardoAI  | `LeonardoAI` | Image                                          |

---

## API HTTP — endpoints clave

| Grupo        | Patron base                                       | Notas                                            |
|--------------|---------------------------------------------------|--------------------------------------------------|
| Auth         | `POST /api/auth/register|login|logout`            | Cookie-based; `GET /api/auth/me`                 |
| ApiKeys      | `GET|POST|PUT|DELETE /api/apikeys`                | Credenciales por proveedor, almacenadas por tenant |
| Rules        | `GET|POST|PUT|DELETE /api/rules`                  | Reglas obligatorias inyectadas en cada ejecucion  |
| Modules      | `GET|POST|PUT|DELETE /api/modules`                | Definiciones de modulos reutilizables + archivos  |
|              | `GET /api/modules/{id}/prompt-history`            | Historial de versiones del prompt del modulo (systemPrompt/imagePrompt); se registra una version en cada `PUT` que cambie el prompt, restaurable desde la UI |
| Projects     | `GET|POST|PUT|DELETE /api/projects`               | Pipeline; incluye graph save y duplicar           |
| Executions   | `POST /api/projects/{id}/execute`                 | Lanza ejecucion                                   |
|              | `POST /api/projects/{id}/cancel`                  | Cancela ejecucion activa                          |
|              | `POST /api/executions/{id}/retry-from-module`     | Reintenta desde un nodo concreto del grafo        |
|              | `GET /api/executions/{id}/logs`                   | Logs persistidos; progreso en tiempo real por SignalR |
| Webhooks     | `POST /api/webhooks/whatsapp|telegram`            | Recepcion de respuestas externas para reanudar interacciones; cada update se deduplica por `update_id` en BD (`ProcessedTelegramUpdates`) para no procesarlo dos veces |
| PromptBuilder| `GET /api/prompt-builder/models`, `POST /api/prompt-builder/questions|compose|add` | Asistente que ayuda a redactar el prompt de un nodo con IA: preguntas de detalle + composicion final, y `add` integra una peticion en el prompt actual (con diff de aceptacion en la UI) |
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
- `docs/DOCUMENTACION.md` — como organizar y mantener los documentos del repo
- `docs/fixes/` — registro de incidencias resueltas (bugs y fixes puntuales)
- `docs/MIGRATION_PLAN_GRAPH_EXECUTOR.md` — decision de diseno del executor basado en grafo
- `docs/MIGRATION_PROGRESS.md` — historial de la migracion al executor actual
- `Server/Services/Ai/GraphPipelineExecutor.cs` — logica de ejecucion, pausas, reintentos y publicacion
- `Server/Services/Ai/ExecutionGraph.cs` — construccion del grafo, propagacion de puertos y fallos en cascada
- `Server/Services/Ai/Handlers/` — un archivo por tipo de modulo
- `Server/Program.cs` — todos los endpoints y configuracion DI
