# Registro de fixes resueltos

Esta carpeta guarda **registros de incidencias ya resueltas**: el problema, la
causa, la solución aplicada y cómo verificarla. Son documentos de referencia
histórica, no documentación viva del sistema.

> Antes de añadir o editar un fix aquí, lee `docs/DOCUMENTACION.md`. Si una
> incidencia deja de aportar valor (se rehízo el módulo, el código ya no existe,
> el problema es irrelevante), **bórrala** en lugar de dejarla obsoleta.

## Índice

| Documento | Resumen |
|-----------|---------|
| [`SYSTEMPROMPT_DINAMICO.md`](SYSTEMPROMPT_DINAMICO.md) | El `systemPrompt` ya no se congela al añadir un módulo al pipeline; se lee del `AiModule`. |
| [`PIPELINE_TIMEOUT_CANCELACION.md`](PIPELINE_TIMEOUT_CANCELACION.md) | Timeouts por provider + timeout de 10 min por módulo y cancelación real desde la UI. |
| [`TEXT_MODULE_IMAGE_INPUT.md`](TEXT_MODULE_IMAGE_INPUT.md) | El módulo de texto acepta entradas de solo imagen (sin prompt de texto). |
| [`BUFFER_HEAD_REQUEST.md`](BUFFER_HEAD_REQUEST.md) | El endpoint público de imágenes de Buffer responde a peticiones `HEAD` (evita el 405). |
| [`TELEGRAM_DUPLICATE_UPDATE.md`](TELEGRAM_DUPLICATE_UPDATE.md) | Idempotencia en BD del `update_id` de Telegram + claim atómico del scheduler para que la interacción no se envíe duplicada. |

## Convención

- Un fichero por incidencia, en `MAYÚSCULAS_CON_GUION_BAJO.md`.
- Cabecera con estado y área afectada:
  `> Estado: **resuelto** · Fecha: AAAA-MM-DD · Área: ruta/del/código`
- Estructura recomendada: **Problema → Causa → Solución → Verificación**.
- Mantén las referencias a código vivas: si borras un módulo o provider, borra o
  actualiza también las menciones en estos docs.
