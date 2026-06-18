# Reglas del proyecto PixelAgents

## Flujo de ramas
- La rama `master` despliega autom·ticamente a producciÛn.
- La rama `develop` despliega autom·ticamente al entorno de preproducciÛn.
- Nunca hacer commits directos en `master`.
- No trabajar directamente en `master` salvo instrucciÛn explÌcita.
- Para nuevas tareas, crear ramas tipo:
  - `feature/nombre-corto`
  - `fix/nombre-corto`
  - `hotfix/nombre-corto`
- Los cambios normales deben partir de `develop` y volver a `develop`.
- Solo lo validado en `develop` debe promocionarse despuÈs a `master`.

## PolÌtica de trabajo
- Antes de cambiar cÛdigo, identificar la rama actual.
- Si est·s en `master`, detenerte y avisar.
- Si la tarea es una feature o bug normal, trabajar desde una rama derivada de `develop`.
- Mostrar siempre un resumen del diff esperado antes de cambios grandes.
- Ejecutar tests/lint relevantes antes de cerrar una tarea.
- No modificar pipeline, CI/CD, secretos o infraestructura salvo peticiÛn explÌcita.

## Estrategia de promociÛn
- feature/fix -> merge a `develop`
- validaciÛn en preproducciÛn
- `develop` -> merge a `master` cuando estÈ aprobado
## Reglas de gesti√≥n
- Todo trabajo debe reflejarse en Leantime.
- No implementar cambios grandes sin tarea creada.
- El tablero debe reflejar el estado real del trabajo.
- Ning√∫n merge a develop o master se hace sin aprobaci√≥n humana.

## Flujo Git
- master = producci√≥n
- develop = preproducci√≥n
- Nunca trabajar directamente en master
- Trabajar en ramas feature/*, fix/* o hotfix/*

## Gesti√≥n de trabajo

- Leantime es la fuente de verdad.
- El humano crea o mueve tareas a Ready.
- El coordinador trabaja solo tareas autorizadas o indicadas por el usuario.
- Estados:
  - Backlog: idea
  - Ready: autorizada para trabajar
  - In Progress: trabajando
  - Review: esperando revisi√≥n humana
  - Blocked: bloqueada
  - Done: cerrada tras aprobaci√≥n
- Ning√∫n merge a develop o master se hace sin aprobaci√≥n humana.

## Subtareas

- El coordinador debe dividir tareas complejas en subtareas.
- Las subtareas tambiÈn deben vivir en Leantime.
- El coordinador puede mover subtareas autÛnomamente entre Backlog, Ready, In Progress, Review y Blocked.
- El usuario solo aprueba cierre final y merges.
- `Done` queda reservado para tareas aprobadas por humano.
## Push autom√°tico seguro

- El movimiento de una tarea a Ready autoriza trabajo autom√°tico y subida de rama.
- Claude puede hacer commit y push √∫nicamente en ramas:
  - feature/*
  - fix/*
  - hotfix/*
- Claude debe usar siempre:
  `./tools/git_safe_commit_push.sh ID_TAREA "mensaje"`
- Est√° prohibido:
  - push directo a master/main/develop
  - merge autom√°tico
  - rebase autom√°tico
  - borrar ramas remotas
  - marcar Done sin aprobaci√≥n humana

## Ramas protegidas

- master = producci√≥n
- develop = preproducci√≥n
- master, main y develop nunca se modifican directamente por agentes.

## Documentaci√≥n (.md)

- La gu√≠a completa est√° en `docs/DOCUMENTACION.md`. S√≠guela al crear o tocar docs.
- Estructura: ra√≠z solo `README.md` y `CLAUDE.md`; documentaci√≥n viva en `docs/`;
  incidencias resueltas en `docs/fixes/`.
- Antes de crear un `.md` nuevo, comprueba si ya existe uno sobre el tema y
  actual√≠zalo en vez de duplicar. No crear "summaries" sueltos en la ra√≠z.
- Antes de editar c√≥digo, revisa si alg√∫n `.md` queda afectado:
  - actualiza los docs que describan lo que cambias;
  - si borras un m√≥dulo/provider/endpoint, elimina sus menciones en los `.md`
    (`rg -i "loQueBorras" --glob '*.md'`);
  - si una incidencia ya resuelta deja de aportar (su c√≥digo ya no existe),
    borra su doc en `docs/fixes/` en lugar de dejarlo obsoleto.
- S√© responsable: deja solo informaci√≥n relevante y borra lo que sea basura o no
  aporte. El historial de git conserva lo eliminado.
