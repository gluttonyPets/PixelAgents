# Reglas del proyecto PixelAgents

## Flujo de ramas
- La rama `master` despliega autom�ticamente a producci�n.
- La rama `develop` despliega autom�ticamente al entorno de preproducci�n.
- Nunca hacer commits directos en `master`.
- No trabajar directamente en `master` salvo instrucci�n expl�cita.
- Para nuevas tareas, crear ramas tipo:
  - `feature/nombre-corto`
  - `fix/nombre-corto`
  - `hotfix/nombre-corto`
- Los cambios normales deben partir de `develop` y volver a `develop`.
- Solo lo validado en `develop` debe promocionarse despu�s a `master`.

## Pol�tica de trabajo
- Antes de cambiar c�digo, identificar la rama actual.
- Si est�s en `master`, detenerte y avisar.
- Si la tarea es una feature o bug normal, trabajar desde una rama derivada de `develop`.
- Mostrar siempre un resumen del diff esperado antes de cambios grandes.
- Ejecutar tests/lint relevantes antes de cerrar una tarea.
- No modificar pipeline, CI/CD, secretos o infraestructura salvo petici�n expl�cita.

## Estrategia de promoci�n
- feature/fix -> merge a `develop`
- validaci�n en preproducci�n
- `develop` -> merge a `master` cuando est� aprobado
## Reglas de gestión
- Todo trabajo debe reflejarse en Leantime.
- No implementar cambios grandes sin tarea creada.
- El tablero debe reflejar el estado real del trabajo.
- Ningún merge a develop o master se hace sin aprobación humana.

## Flujo Git
- master = producción
- develop = preproducción
- Nunca trabajar directamente en master
- Trabajar en ramas feature/*, fix/* o hotfix/*

## Gestión de trabajo

- Leantime es la fuente de verdad.
- El humano crea o mueve tareas a Ready.
- El coordinador trabaja solo tareas autorizadas o indicadas por el usuario.
- Estados:
  - Backlog: idea
  - Ready: autorizada para trabajar
  - In Progress: trabajando
  - Review: esperando revisión humana
  - Blocked: bloqueada
  - Done: cerrada tras aprobación
- Ningún merge a develop o master se hace sin aprobación humana.

## Subtareas

- El coordinador debe dividir tareas complejas en subtareas.
- Las subtareas tambi�n deben vivir en Leantime.
- El coordinador puede mover subtareas aut�nomamente entre Backlog, Ready, In Progress, Review y Blocked.
- El usuario solo aprueba cierre final y merges.
- `Done` queda reservado para tareas aprobadas por humano.
## Push automático seguro

- El movimiento de una tarea a Ready autoriza trabajo automático y subida de rama.
- Claude puede hacer commit y push únicamente en ramas:
  - feature/*
  - fix/*
  - hotfix/*
- Claude debe usar siempre:
  `./tools/git_safe_commit_push.sh ID_TAREA "mensaje"`
- Está prohibido:
  - push directo a master/main/develop
  - merge automático
  - rebase automático
  - borrar ramas remotas
  - marcar Done sin aprobación humana

## Ramas protegidas

- master = producción
- develop = preproducción
- master, main y develop nunca se modifican directamente por agentes.

## Documentación (.md)

- La guía completa está en `docs/DOCUMENTACION.md`. Síguela al crear o tocar docs.
- Estructura: raíz solo `README.md` y `CLAUDE.md`; documentación viva en `docs/`;
  incidencias resueltas en `docs/fixes/`.
- Antes de crear un `.md` nuevo, comprueba si ya existe uno sobre el tema y
  actualízalo en vez de duplicar. No crear "summaries" sueltos en la raíz.
- Antes de editar código, revisa si algún `.md` queda afectado:
  - actualiza los docs que describan lo que cambias;
  - si borras un módulo/provider/endpoint, elimina sus menciones en los `.md`
    (`rg -i "loQueBorras" --glob '*.md'`);
  - si una incidencia ya resuelta deja de aportar (su código ya no existe),
    borra su doc en `docs/fixes/` en lugar de dejarlo obsoleto.
- Sé responsable: deja solo información relevante y borra lo que sea basura o no
  aporte. El historial de git conserva lo eliminado.

## Código muerto

- Si al trabajar detectas código muerto (métodos, componentes, modales, campos,
  endpoints o DTOs que nada usa), elimínalo en lugar de dejarlo.
- Antes de borrar, comprueba que de verdad no tiene usos: búsqueda en el
  repo (cliente, servidor, tests, scripts de `automation/` y `tools/`) y
  avisos del compilador (`CS0169`, `CS0414`).
- Hazlo en una rama aparte (`fix/limpiar-...`), no mezclado con la feature.
- Actualiza los `.md` que mencionen lo borrado.
- Si hay duda razonable de que algo externo lo use (p. ej. un endpoint
  público), no lo borres: pregúntalo.
