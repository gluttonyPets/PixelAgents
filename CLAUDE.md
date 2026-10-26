# Reglas del proyecto PixelAgents

## Flujo de ramas
- La rama `master` despliega automáticamente a producción.
- La rama `develop` despliega automáticamente al entorno de preproducción.
- Nunca hacer commits directos en `master`.
- No trabajar directamente en `master` salvo instrucción explícita.
- Para nuevas tareas, crear ramas tipo:
  - `feature/nombre-corto`
  - `fix/nombre-corto`
  - `hotfix/nombre-corto`
- Los cambios normales deben partir de `develop` y volver a `develop`.
- Solo lo validado en `develop` debe promocionarse después a `master`.

## Política de trabajo
- Antes de cambiar código, identificar la rama actual.
- Si estás en `master`, detenerte y avisar.
- Si la tarea es una feature o bug normal, trabajar desde una rama derivada de `develop`.
- Mostrar siempre un resumen del diff esperado antes de cambios grandes.
- Ejecutar tests/lint relevantes antes de cerrar una tarea.
- No modificar pipeline, CI/CD, secretos o infraestructura salvo petición explícita.

## Estrategia de promoción
- feature/fix -> merge a `develop`
- validación en preproducción
- `develop` -> merge a `master` cuando esté aprobado

## Reglas de gestión
- Ningún merge a develop o master se hace sin aprobación humana.

## Flujo Git
- master = producción
- develop = preproducción
- Nunca trabajar directamente en master
- Trabajar en ramas feature/*, fix/* o hotfix/*

## Push seguro

- Claude puede hacer commit y push únicamente en ramas:
  - feature/*
  - fix/*
  - hotfix/*
- Está prohibido:
  - push directo a master/main/develop
  - merge automático
  - rebase automático
  - borrar ramas remotas

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
