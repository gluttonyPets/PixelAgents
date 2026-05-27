# 🐛 Bug Fix: System Prompt Congelado

## Resumen Ejecutivo

**Problema**: Los cambios al System Prompt en "Módulos AI" no se reflejaban en pipelines que ya tenían el módulo añadido.

**Causa**: El `systemPrompt` se copiaba al `ProjectModule` cuando se añadía al pipeline, "congelándolo" en ese momento.

**Solución**: Ya no se copia el `systemPrompt` al añadir módulos. Ahora se lee dinámicamente del `AiModule`, permitiendo actualizaciones globales.

---

## ✅ Archivos Modificados

### 1. Server/Program.cs
- ✅ Nueva función `CopyConfigurationExcludingSystemPrompt()` (línea ~272)
- ✅ Modificado endpoint `POST /api/projects/{projectId}/modules` (línea ~1363)
- ✅ Modificado lógica de clonación de proyectos (línea ~1272)

### 2. Nuevos Archivos Creados

- ✅ `docs/BUG_FIX_SYSTEMPROMPT.md` - Documentación completa del fix
- ✅ `tools/migrate_remove_frozen_systemprompts.sql` - Script SQL de migración
- ✅ `tools/MigrateSystemPrompts.cs` - Herramienta C# de migración

---

## 🚀 Próximos Pasos

### 1. Probar el Fix (Desarrollo)
```bash
# Compilar
dotnet build Server/Server.csproj

# Ejecutar tests si existen
dotnet test

# Iniciar servidor
dotnet run --project Server/Server.csproj
```

### 2. Migrar Datos Existentes

**Opción A - SQL directo**:
```bash
# Para cada tenant
psql -d tenant_abc -f tools/migrate_remove_frozen_systemprompts.sql
psql -d tenant_xyz -f tools/migrate_remove_frozen_systemprompts.sql
```

**Opción B - Desde código C#**:
Integrar `MigrateSystemPrompts.cs` y ejecutar la migración al inicio de la aplicación.

### 3. Verificar el Fix

1. Modifica un System Prompt en "Módulos AI"
2. Ejecuta un pipeline que use ese módulo
3. Verifica en los logs que el nuevo prompt se usa correctamente

**Query de verificación**:
```sql
-- Debería retornar 0 filas después de la migración
SELECT COUNT(*) FROM "ProjectModules"
WHERE "Configuration" IS NOT NULL
  AND "Configuration"::JSONB ? 'systemPrompt';
```

### 4. Desplegar a Producción

1. ✅ Revisar y aprobar los cambios
2. ✅ Hacer backup de las bases de datos
3. ✅ Desplegar el código actualizado
4. ✅ Ejecutar la migración en todas las bases de datos tenant
5. ✅ Verificar que no hay errores
6. ✅ Comunicar a los usuarios el fix

---

## 📊 Impacto

### Usuarios Afectados
- ✅ **Todos los usuarios** que hayan modificado un System Prompt después de añadir el módulo a un pipeline

### Beneficios
- ✅ System Prompts se actualizan globalmente
- ✅ Menos confusión al modificar prompts
- ✅ Mejor mantenibilidad de módulos AI
- ✅ Comportamiento más intuitivo

### Riesgos
- ⚠️ **Bajo**: Si alguien dependía del comportamiento "congelado", el prompt puede cambiar
- ⚠️ **Mitigation**: Los usuarios pueden añadir `systemPrompt` manualmente en configuración del ProjectModule si necesitan un prompt específico

---

## 🧪 Testing Sugerido

### Test 1: Módulo Nuevo
1. Crear un módulo AI con systemPrompt "Prompt Original"
2. Añadirlo a un pipeline
3. Ejecutar el pipeline → Debe usar "Prompt Original"
4. Modificar el systemPrompt a "Prompt Actualizado"
5. Ejecutar el pipeline nuevamente → Debe usar "Prompt Actualizado" ✅

### Test 2: Módulo Existente (Después de Migración)
1. Ejecutar migración en un módulo existente
2. Verificar que el systemPrompt fue removido del ProjectModule
3. Modificar el systemPrompt en el AiModule
4. Ejecutar el pipeline → Debe usar el nuevo prompt ✅

### Test 3: Override Manual
1. Añadir un módulo a un pipeline
2. Manualmente añadir `systemPrompt` en la configuración del ProjectModule
3. Ejecutar → Debe usar el prompt del ProjectModule (override) ✅

### Test 4: Clonación de Proyectos
1. Clonar un proyecto
2. Verificar que el proyecto clonado NO tiene systemPrompt en ProjectModule.Configuration
3. Modificar el systemPrompt del AiModule original
4. Ejecutar ambos proyectos → Ambos deben usar el nuevo prompt ✅

---

## 📞 Contacto

Si encuentras problemas con este fix:
1. Revisa la documentación completa en `docs/BUG_FIX_SYSTEMPROMPT.md`
2. Verifica que la migración se ejecutó correctamente
3. Reporta el issue con logs detallados

---

**Estado**: ✅ Fix implementado y listo para testing  
**Fecha**: 2026-05-27  
**Revisión requerida**: Sí (antes de desplegar a producción)
