# Bug Fix: System Prompt No Se Actualiza en Módulos de Pipeline

## 🐛 Descripción del Bug

Cuando añadías un módulo AI a un pipeline, el `systemPrompt` configurado en el módulo se "congelaba" en ese momento. Si posteriormente modificabas el `systemPrompt` desde "Módulos AI", el cambio **NO surtía efecto** en los pipelines que ya tenían ese módulo añadido.

### ¿Por qué ocurría esto?

1. **Al añadir un módulo al pipeline**: Se copiaba TODA la configuración del `AiModule` (incluyendo `systemPrompt`) al `ProjectModule`
2. **Durante la ejecución**: Se hacía un merge donde `ProjectModule.Configuration` sobrescribía `AiModule.Configuration`
3. **Al actualizar el Module AI**: Solo se actualizaba `AiModule.Configuration`, pero los `ProjectModule` existentes mantenían su copia antigua

**Resultado**: El `systemPrompt` antiguo (del `ProjectModule`) sobrescribía el nuevo (del `AiModule`) en cada ejecución.

## ✅ Solución Implementada

### Cambios en el Código

**1. Nueva función helper** (`Program.cs` línea ~265):
```csharp
static string? CopyConfigurationExcludingSystemPrompt(string? sourceConfig)
```
Esta función copia la configuración de un módulo pero **excluye el campo `systemPrompt`**, permitiendo que este se lea siempre del `AiModule` actualizado.

**2. Modificación al añadir módulos** (`Program.cs` línea ~1363):
```csharp
Configuration = req.Configuration ?? CopyConfigurationExcludingSystemPrompt(module.Configuration)
```
Ahora cuando añades un módulo a un pipeline, se copian los parámetros de configuración (temperature, numberOfImages, etc.) PERO NO el `systemPrompt`.

**3. Modificación al clonar proyectos** (`Program.cs` línea ~1272):
```csharp
Configuration = CopyConfigurationExcludingSystemPrompt(pm.Configuration)
```
Los proyectos clonados también heredan dinámicamente el `systemPrompt` del módulo base.

### Comportamiento Después del Fix

✅ **System Prompts dinámicos**: Al modificar el `systemPrompt` en "Módulos AI", el cambio se aplica inmediatamente a TODOS los pipelines que usan ese módulo

✅ **Personalización por pipeline**: Si necesitas un `systemPrompt` diferente para un pipeline específico, aún puedes añadirlo manualmente en la configuración del `ProjectModule`

✅ **Otros parámetros funcionan igual**: Los demás campos de configuración (temperature, maxTokens, numberOfImages, etc.) siguen permitiendo personalización por pipeline

## 🔧 Migración de Datos Existentes

Los módulos que ya están añadidos a pipelines **todavía tienen el `systemPrompt` congelado** en su configuración. Debes ejecutar un script de migración para limpiarlos.

### Opción 1: Script SQL (PostgreSQL)

Ejecuta el script para cada base de datos de tenant:

```bash
psql -d tenant_xxx -f tools/migrate_remove_frozen_systemprompts.sql
```

Este script:
- Elimina el campo `systemPrompt` de todos los `ProjectModule.Configuration`
- Preserva los demás campos de configuración
- Actualiza el timestamp `UpdatedAt`

### Opción 2: Herramienta C#

Integra el código de `tools/MigrateSystemPrompts.cs` en tu aplicación y ejecuta:

```csharp
var tenantDbNames = new[] { "tenant_abc", "tenant_xyz" };
await MigrateSystemPrompts.RunAsync(factory, tenantDbNames);
```

### Opción 3: Migración Manual desde la UI

1. Ve a cada proyecto afectado
2. Selecciona el módulo en el pipeline
3. En la configuración del módulo, elimina el campo `systemPrompt` si existe
4. Guarda los cambios

## 📋 Verificación

Para verificar que el fix funciona:

1. **Modifica un System Prompt** en "Módulos AI"
2. **Ejecuta un pipeline** que use ese módulo
3. **Verifica en los logs** que el nuevo prompt se está usando

También puedes verificar en la base de datos:

```sql
-- Ver ProjectModules que todavía tienen systemPrompt congelado
SELECT pm."Id", pm."StepName", pm."Configuration"
FROM "ProjectModules" pm
WHERE pm."Configuration" IS NOT NULL
  AND pm."Configuration"::JSONB ? 'systemPrompt';
```

## 🎯 Casos de Uso

### Caso 1: Quiero que todos mis pipelines usen el mismo prompt actualizado
✅ **Solución**: Define el `systemPrompt` en el módulo AI. Todos los pipelines lo heredarán automáticamente.

### Caso 2: Quiero un prompt diferente para un pipeline específico
✅ **Solución**: Añade `systemPrompt` manualmente en la configuración de ese `ProjectModule` específico.

### Caso 3: Tengo múltiples versiones de un módulo con diferentes prompts
✅ **Solución**: Crea múltiples módulos AI (ej: "Texto GPT4 - Formal", "Texto GPT4 - Casual") cada uno con su `systemPrompt`.

## 📝 Notas Técnicas

### Prioridad de Configuración

El merge de configuración sigue este orden (último gana):
1. `AiModule.Configuration` (base)
2. `ProjectModule.Configuration` (overrides específicos del pipeline)

Si un `ProjectModule` tiene `systemPrompt` definido explícitamente, este **sobrescribe** el del `AiModule`.

### Campos que se Copian al Añadir un Módulo

Estos campos SÍ se copian del `AiModule` al `ProjectModule` cuando añades un módulo:
- `temperature`
- `maxTokens`
- `numberOfImages`
- `n`
- `modelName`
- Cualquier otro campo EXCEPTO `systemPrompt`

Esto permite que cada pipeline pueda personalizar estos parámetros sin afectar al módulo base.

## 🔍 Archivos Modificados

1. **Server/Program.cs**
   - Nueva función: `CopyConfigurationExcludingSystemPrompt()` (~línea 272)
   - Modificado: Endpoint POST `/api/projects/{projectId}/modules` (~línea 1363)
   - Modificado: Endpoint POST `/api/projects/clone` (~línea 1272)

2. **tools/migrate_remove_frozen_systemprompts.sql** (nuevo)
   - Script SQL para migración automática

3. **tools/MigrateSystemPrompts.cs** (nuevo)
   - Herramienta C# para migración

## 📅 Historial

- **Fecha del Bug Report**: 2026-05-27
- **Fecha del Fix**: 2026-05-27
- **Versión Afectada**: Todas las versiones anteriores
- **Versión Corregida**: A partir de este commit

---

**Autor del Fix**: OpenHands AI Agent  
**Reportado por**: Usuario (issue original en español)
