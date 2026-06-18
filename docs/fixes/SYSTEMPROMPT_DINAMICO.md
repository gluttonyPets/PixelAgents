# Fix: System Prompt dinámico (no se congela al añadir el módulo)

> Estado: **resuelto** · Fecha: 2026-05-27 · Área: `Server/Program.cs`

## Problema

Al añadir un módulo AI a un pipeline, el `systemPrompt` configurado en el módulo
se "congelaba" en ese momento. Si después modificabas el `systemPrompt` desde
"Módulos AI", el cambio **no surtía efecto** en los pipelines que ya tenían ese
módulo añadido.

### Causa raíz

1. Al añadir el módulo al pipeline se copiaba **toda** la configuración del
   `AiModule` (incluido `systemPrompt`) al `ProjectModule`.
2. Durante la ejecución se hacía un merge donde `ProjectModule.Configuration`
   sobrescribía `AiModule.Configuration`.
3. Al actualizar el módulo AI solo cambiaba `AiModule.Configuration`; los
   `ProjectModule` existentes conservaban su copia antigua.

Resultado: el `systemPrompt` viejo (del `ProjectModule`) sobrescribía el nuevo
(del `AiModule`) en cada ejecución.

## Solución

Ya no se copia el `systemPrompt` al añadir o clonar módulos. Se lee siempre de
forma dinámica del `AiModule`, permitiendo actualizaciones globales.

Cambios en `Server/Program.cs`:

- **Nueva función helper** (`~línea 272`): `CopyConfigurationExcludingSystemPrompt(string? sourceConfig)`
  copia los parámetros de configuración (temperature, maxTokens, numberOfImages,
  modelName, etc.) **excluyendo** `systemPrompt`.
- **POST `/api/projects/{projectId}/modules`** (`~línea 1363`):
  `Configuration = req.Configuration ?? CopyConfigurationExcludingSystemPrompt(module.Configuration)`
- **Clonación de proyectos** (`~línea 1272`):
  `Configuration = CopyConfigurationExcludingSystemPrompt(pm.Configuration)`

### Comportamiento resultante

- **Global**: al modificar el `systemPrompt` en "Módulos AI", el cambio se aplica
  a todos los pipelines que usan ese módulo.
- **Override por pipeline**: si necesitas un `systemPrompt` distinto para un
  pipeline concreto, puedes añadirlo manualmente en la configuración de ese
  `ProjectModule` (sigue teniendo prioridad sobre el del `AiModule`).
- El resto de parámetros (temperature, maxTokens, etc.) se siguen copiando y
  permiten personalización por pipeline.

## Migración de datos existentes

Los módulos ya añadidos a pipelines antes del fix **todavía tienen el
`systemPrompt` congelado** y hay que limpiarlos una sola vez:

```bash
# Opción A — SQL directo, por cada BD de tenant
psql -d tenant_xxx -f tools/migrate_remove_frozen_systemprompts.sql
```

```csharp
// Opción B — herramienta C# (tools/MigrateSystemPrompts.cs)
var tenantDbNames = new[] { "tenant_abc", "tenant_xyz" };
await MigrateSystemPrompts.RunAsync(factory, tenantDbNames);
```

El script elimina `systemPrompt` de cada `ProjectModule.Configuration`,
preserva el resto de campos y actualiza `UpdatedAt`.

## Verificación

```sql
-- Debe devolver 0 filas tras la migración
SELECT COUNT(*) FROM "ProjectModules"
WHERE "Configuration" IS NOT NULL
  AND "Configuration"::JSONB ? 'systemPrompt';
```

1. Modifica un System Prompt en "Módulos AI".
2. Ejecuta un pipeline que use ese módulo.
3. Comprueba en los logs que se usa el nuevo prompt.
