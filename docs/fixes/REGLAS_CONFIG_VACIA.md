# Fix: los modulos ensenaban reglas activas y Configuracion -> Reglas salia vacia

> Estado: **resuelto** · Fecha: 2026-09-09 · Área: `Client/Components/Settings/Rules.razor`,
> `Client/Models/PipelineGraphModels.cs`, `Client/Services/ApiClient.cs`

## Problema

En el inspector de un modulo (texto, coordinador, orquestador) aparecia el bloque
**"Reglas activas"** con varias reglas, pero la pestana `reglas` de `/configuracion`
mostraba "Sin reglas configuradas". Parecia que la pantalla de reglas no cargaba nada.

## Causa raiz

Son dos catalogos distintos y la UI no lo decia en ninguna parte:

1. **Reglas propias del tenant**: filas de la tabla `Rules` de la BD del tenant,
   que es lo unico que lista `GET /api/rules` y, por tanto, lo unico que ensenaba
   Configuracion -> Reglas. Solo se siembra una regla por defecto al **crear la
   cuenta** (`AccountService.SeedDefaultRulesAsync`), asi que un tenant anterior a
   la feature tiene la tabla creada (la crea `TenantDbContextFactory`) pero vacia.
2. **Reglas integradas**: constantes de compilacion de
   `Server/Services/Ai/OutputSchema.cs` (`GetTextContentRules`: comportamiento,
   formato ASCII y veto de marcas) que `SystemPromptComposer` antepone en todo
   modulo de texto. El inspector las pintaba desde su espejo en el cliente
   (`ActiveRulesRegistry`), sin marcarlas como integradas, y no estaban en ninguna
   pantalla de configuracion.

Ademas, un fallo de carga se veia **exactamente igual que no tener reglas**:
`ApiClient.GetRulesAsync` devolvia lista vacia ante cualquier respuesta no 2xx y
`Rules.razor` hacia `catch { }`, de modo que un 401 (claim `db_name` ausente) o un
500 acababan tambien en el mensaje "Sin reglas configuradas".

## Solucion

- `ActiveRulesRegistry.BuiltInTextRules`: las reglas integradas se extraen a un
  catalogo publico reutilizable (mismo texto que antes, sin duplicarlo).
- Configuracion -> Reglas lista ahora esas reglas integradas en una tabla de solo
  lectura, debajo de las propias, con la etiqueta "Integrada".
- `ApiClient.GetRulesAsync` devuelve `(Rules, Error)` y `Rules.razor` distingue
  "no hay reglas propias" de "no se pudieron cargar", con aviso y boton de
  reintentar.
- El estado vacio explica que las reglas que el inspector marca como activas sin
  aparecer en el listado son las integradas.

No se toca el sembrado de reglas por defecto: sembrar en cada apertura de la BD del
tenant resucitaria una regla que el usuario haya borrado a proposito.

## Verificacion

1. `/configuracion/reglas` con la tabla `Rules` vacia: sale "Sin reglas propias" y,
   debajo, las tres reglas integradas.
2. Crear una regla: aparece arriba y tambien en el inspector del modulo, bajo
   "Reglas del proyecto".
3. Forzar un fallo del endpoint (sesion caducada, servidor caido): sale
   "No se pudieron cargar las reglas: ..." en vez del estado vacio.
