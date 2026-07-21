# Sub-proyectos encadenados

Permite insertar **un proyecto entero como un único módulo** dentro del pipeline de
otro proyecto. Por ejemplo: un proyecto "Crear blog" puede insertarse dentro del
proyecto "Publicar en Instagram" y comportarse como un paso más.

## Comportamiento

- El nodo es de tipo `SubProject` y referencia a otro `Project` (referencia **viva**:
  se ejecuta la definición actual del proyecto insertado, no una copia).
- La entrada que llega al nodo se usa como `UserInput` del proyecto insertado, igual
  que si fuera su módulo de **Inicio**.
- Se ejecutan **todos** los módulos del proyecto insertado como una **ejecución hija**
  aislada (su propio `ProjectExecution`), y la salida de su(s) nodo(s) **terminal(es)**
  (los que no tienen conexiones salientes) vuelve al pipeline padre como salida del nodo.
- Los archivos producidos por el proyecto insertado se copian a la ejecución padre para
  que los módulos siguientes puedan resolverlos con normalidad.
- Puertos del nodo: una entrada (`input`) y una salida (`output`).

## Límites de la v1

- **Sin módulos interactivos** dentro de un sub-proyecto: si el proyecto insertado
  contiene módulos que pausan la ejecución (`Interaction`, `Checkpoint`), el nodo falla
  con un mensaje claro. La ejecución hija es síncrona respecto al nodo padre y no soporta
  pausas humanas anidadas.
- **Sin recursión**: no se puede insertar el mismo proyecto en sí mismo ni crear ciclos
  indirectos (A→B→A). La validación se hace al insertar el nodo. Además hay una guarda de
  profundidad en ejecución (máx. 5 niveles) como defensa.

## UI

- En el editor visual, junto a **"+ Modulo"** hay un botón **"+ Sub-proyecto"** que abre
  un selector con los proyectos disponibles (excluye el actual).
- El nodo se dibuja como una **tarjeta grande** que lista los pasos internos del proyecto
  insertado, para ver de un vistazo qué hace.
- El inspector del nodo muestra el proyecto insertado, sus pasos y un enlace para abrirlo.

## Piezas técnicas

- Modelo: `ProjectModule.SubProjectId` (`uuid?`, FK a `Projects` con `ON DELETE SET NULL`).
  Migración manual en `TenantDbContextFactory.ApplyPendingColumns`.
- Módulo de sistema `SubProject` en `SystemModuleCatalog` (oculto del palette "+ Modulo").
- Handler: `Server/Services/Ai/Handlers/SubProjectModuleHandler.cs`. Ejecuta el proyecto
  insertado mediante `IPipelineExecutor` en un scope nuevo y un `UserDbContext` propio.
- Endpoint: `POST /api/projects/{projectId}/subprojects` (body `AddSubProjectRequest`),
  con validación de ciclos (`WouldCreateSubProjectCycleAsync`).
- El nodo `SubProject` queda exento del timeout de 10 min por módulo del executor, porque
  ejecuta un pipeline entero (cada módulo interno mantiene su propio timeout).
- Cliente: puertos/icono/color en `PipelineGraphModels`, botón y selector en
  `PipelineCanvas.razor`, tarjeta en `wwwroot/js/pipeline-editor.js`.
