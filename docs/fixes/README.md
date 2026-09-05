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
| [`SHOPIFY_IMAGE_URL.md`](SHOPIFY_IMAGE_URL.md) | La imagen destacada fallaba con "Invalid URL provided" porque Shopify no podía descargarla de `http://IP:8080`; ahora se sube directa a Shopify con `stagedUploadsCreate`. |
| [`TELEGRAM_DUPLICATE_UPDATE.md`](TELEGRAM_DUPLICATE_UPDATE.md) | Idempotencia en BD del `update_id` de Telegram + claim atómico del scheduler para que la interacción no se envíe duplicada. |
| [`CONFIG_CASILLAS_BOOLEANAS.md`](CONFIG_CASILLAS_BOOLEANAS.md) | Las casillas del inspector se guardan como cadena y `GetConfigBool` solo leía booleanos JSON: "Publicar" no se aplicaba. ShopifyBlog publica visible por defecto. |
| [`COSTE_EJECUCION_PRICING.md`](COSTE_EJECUCION_PRICING.md) | El gasto salía 0 (oculto) porque faltaban modelos actuales en `PricingCatalog`; después, el que sí salía usaba la tarifa del hermano más antiguo de la familia. La API no da coste por petición, solo tokens. |
| [`COSTE_TOKENS_OPENAI.md`](COSTE_TOKENS_OPENAI.md) | Las imágenes gpt-image salían siempre en `high` (90 % de la factura) porque la UI ofrecía valores de DALL-E y nunca escribía `quality`; + contexto duplicado en el planner, orden del system prompt anti-caché y `reasoning_effort` inexistente. |
| [`SHOPIFY_HANDLE_DUPLICADO.md`](SHOPIFY_HANDLE_DUPLICADO.md) | Shopify rechazaba el artículo con "Handle has already been taken" al repetirse el slug; ahora se reintenta con sufijo (`-2`, `-3`, fecha) y solo se avisa en el log. |
| [`IMAGEN_MULTIPLE_N.md`](IMAGEN_MULTIPLE_N.md) | Un modulo de imagen con varias salidas devolvia la misma composicion repetida: `n` son muestras del mismo prompt, no partes. Ahora se reparte el texto por escenas y se hace una llamada por imagen. |

## Convención

- Un fichero por incidencia, en `MAYÚSCULAS_CON_GUION_BAJO.md`.
- Cabecera con estado y área afectada:
  `> Estado: **resuelto** · Fecha: AAAA-MM-DD · Área: ruta/del/código`
- Estructura recomendada: **Problema → Causa → Solución → Verificación**.
- Mantén las referencias a código vivas: si borras un módulo o provider, borra o
  actualiza también las menciones en estos docs.
