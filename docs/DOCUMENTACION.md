# Guía de documentación de PixelAgents

Cómo organizar, crear y mantener los `.md` del repositorio. El objetivo es que la
documentación esté **limpia, vigente y sin basura**: solo lo que aporta valor.

---

## 1. Dónde va cada cosa

```
/                        Solo los dos documentos de entrada al repo:
  README.md              guía técnica de onboarding (visión general del sistema)
  CLAUDE.md              reglas de trabajo para agentes (ramas, flujo, permisos)

/docs/                   Documentación viva del proyecto:
  DOCUMENTACION.md       esta guía
  architecture.md        arquitectura, capas, modelo de datos
  *_SETUP.md             guías de configuración (Buffer, Leantime MCP, ...)
  MIGRATION_*.md         planes y progreso de migraciones en curso

/docs/fixes/             Registro histórico de incidencias resueltas:
  README.md              índice de fixes
  *.md                   un fichero por incidencia (problema → solución)

/tools/                  Scripts y utilidades; su propio README.md
/.claude/agents/         Definiciones de subagentes (no tocar como "docs")
```

Regla rápida para decidir dónde poner un `.md`:

- **Explica cómo funciona o se configura algo del sistema hoy** → `docs/`.
- **Documenta una incidencia ya resuelta** (bug, fix puntual) → `docs/fixes/`.
- **Es una regla de trabajo para agentes** → va en `CLAUDE.md`, no un doc nuevo.
- **Es la puerta de entrada del repo** → `README.md`.

No crear documentos de "fix" ni "summary" sueltos en la raíz. La raíz solo tiene
`README.md` y `CLAUDE.md`.

---

## 2. Antes de crear un `.md` nuevo

Sé conservador: **la mayoría de cambios no necesitan un documento nuevo.**

1. ¿Existe ya un doc sobre este tema? → **actualízalo**, no crees otro.
   (Evita duplicados como "X_FIX" y "X_FIX_SUMMARY" sobre la misma cosa.)
2. ¿Cabe como sección en un doc existente? → añádela ahí.
3. ¿Es información efímera (notas de una sesión, TODO temporal)? → no la
   documentes en el repo.
4. Solo si nada de lo anterior aplica, crea un fichero nuevo en la carpeta
   correcta y añádelo al índice correspondiente (p. ej. `docs/fixes/README.md`).

---

## 3. Antes de editar código, revisa la documentación afectada

**Siempre que toques código, comprueba si algún `.md` queda afectado** y actúa:

- Si un doc describe algo que estás cambiando → **actualízalo en el mismo cambio**.
- Si eliminas un módulo, provider, endpoint o feature → **busca y borra o
  actualiza** todas las menciones en los `.md` (no dejes referencias muertas):

  ```bash
  rg -i "NombreDeLoQueBorras" --glob '*.md'
  ```

- Si un doc de `docs/fixes/` describe una incidencia que **ya no aplica** (el
  código se rehízo, el módulo desapareció, el problema es irrelevante) →
  **bórralo**. Un fix resuelto cuyo código ya no existe es basura, no historia.

---

## 4. Mantener limpio: qué conservar y qué borrar

Conserva solo lo que ayuda a entender o mantener el sistema:

**Conservar**
- El problema, la causa y la solución de forma concisa.
- Referencias a archivos/líneas de código que siguen existiendo.
- Comandos de verificación o migración reutilizables.

**Borrar / no escribir**
- Checklists de despliegue de una sola vez ya cumplidos
  ("desplegar a producción", "comunicar a usuarios", casillas marcadas).
- Secciones de "próximos pasos" especulativos que nunca se harán.
- Relleno decorativo, agradecimientos, datos de contacto, emojis de adorno.
- Cualquier referencia a código que ya no existe.

Si dudas entre dejar un doc obsoleto "por si acaso" o borrarlo: **bórralo**. El
historial de git conserva todo lo eliminado.

---

## 5. Formato de un fix

Cada documento en `docs/fixes/` debería seguir esta plantilla:

```markdown
# Fix: título corto en minúsculas

> Estado: **resuelto** · Fecha: AAAA-MM-DD · Área: ruta/del/codigo.cs

## Problema
Qué fallaba y cómo se reproducía.

## Causa
Por qué ocurría.

## Solución
Qué se cambió (con rutas/líneas de código vivas).

## Verificación
Cómo comprobar que funciona.
```

---

## 6. Checklist al cerrar una tarea

- [ ] ¿Algún `.md` describe algo que cambié? → actualizado.
- [ ] ¿Borré código? → sin referencias muertas en los `.md` (`rg` en `*.md`).
- [ ] ¿Una incidencia quedó obsoleta? → su doc en `docs/fixes/` borrado.
- [ ] ¿Creé un doc nuevo? → en la carpeta correcta y añadido a su índice.
- [ ] ¿La raíz sigue teniendo solo `README.md` y `CLAUDE.md`?
