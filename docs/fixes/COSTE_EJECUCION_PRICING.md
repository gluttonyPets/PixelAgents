# Fix: el gasto de cada ejecución no se mostraba

> Estado: **resuelto** · Fecha: 2026-07-08 · Área: `Server/Services/Ai/PricingCatalog.cs`

## Problema

En el inspector de ejecuciones, el gasto (`$X.XXXX`) de la mayoría de pasos y del
total no aparecía. La UI solo pinta el coste cuando es mayor que cero
(`@if (step.EstimatedCost > 0)` en `Client/Pages/ProjectDetail.razor`), así que un
coste de `0` se traduce en "no se muestra nada".

## Causa raíz

El coste **no** se pide a la API del proveedor: no existe tal cosa (ver más abajo).
El flujo real es:

1. Cada provider (`OpenAiProvider`, `AnthropicProvider`, `GeminiProvider`,
   `GrokProvider`, …) lee los **tokens reales** del `usage` de la respuesta.
2. `PricingCatalog.EstimateTextCost` / `EstimateImageCost` multiplican esos tokens
   por una tabla de precios *hardcodeada*.
3. El coste se guarda por paso (`StepExecution.EstimatedCost`) y se agrega en
   `ProjectExecution.TotalEstimatedCost`.

El `PricingCatalog` estaba **desactualizado respecto al `ModelCatalog`**: se ofrecían
al usuario modelos que no existían en la tabla de precios ni hacían match por prefijo,
así que `EstimateTextCost`/`EstimateImageCost` devolvían `0`:

- **Anthropic**: Opus 4.6, Sonnet 4.6, Opus 4.5, Sonnet 4.5, Haiku 4.5, Opus 4.1
  (todos los modelos Claude actuales) → coste 0.
- **Gemini texto**: `gemini-1.5-pro`, `gemini-1.5-flash` → coste 0.
- **Gemini imagen**: `gemini-2.5-flash-image`, `gemini-3.1-flash-image-preview`,
  `gemini-3-pro-image-preview` → coste 0.

## Solución implementada

Se añadieron las entradas que faltaban en `PricingCatalog`, con precios por 1M de
tokens (texto) o por imagen (imagen), en USD. Para Anthropic se usan **claves
cortas** (`claude-opus-4-5`) que hacen match por prefijo con los IDs con fecha
(`claude-opus-4-5-20251124`). `gemini-2.0-flash-lite` se coloca **antes** de
`gemini-2.0-flash` para que el match por prefijo no lo capture el más genérico.

Al mantener el `ModelCatalog` sincronizado con el `PricingCatalog`, todos los
modelos ofrecidos producen ahora un coste > 0 y la UI lo muestra.

> **Nota de mantenimiento**: al añadir un modelo nuevo en `ModelCatalog` (o en
> `Client/Pages/Modules.razor`), añade también su precio en `PricingCatalog`, o el
> gasto se mostrará como 0.

## ¿Se puede pedir el coste a la API? (investigación)

No, no de forma útil por ejecución:

- Los endpoints de generación (OpenAI chat/images, Anthropic Messages, Gemini,
  xAI Grok) **no devuelven el coste en dólares**; solo devuelven `usage` con
  tokens (input/output, y en algunos casos cacheados). Eso es justo lo que ya
  consumimos.
- Las únicas APIs de coste real son informes **agregados a nivel de organización**
  (p.ej. Anthropic *Usage & Cost Admin API*, OpenAI *Costs API*), que:
  - requieren una **clave de administrador** distinta de la de la API normal,
  - están **agregados por día**, no por petición,
  - tienen retardo de facturación.

  No sirven para atribuir el gasto a una ejecución concreta.

Conclusión: la estimación tokens × tarifa es el enfoque correcto; su precisión
depende únicamente de que la tabla de precios esté completa y al día (por eso el
importe se muestra con el prefijo `~$`, indicando que es aproximado).
