# Fix: las imágenes se generaban siempre en calidad `high` y se pagaba de más

> Estado: **resuelto** · Fecha: 2026-08-06 · Área: `Server/Services/Ai/`, `Client/Pages/Modules.razor`, `Client/Components/ModuleConfigEditor.razor`

## Problema

Un análisis del consumo real de OpenAI (30 días, 288 peticiones, ~16 $) mostró que
el **90 % del gasto eran imágenes**, no texto. Un día típico:

| Concepto | Coste | % |
|----------|-------|---|
| 2 imágenes gpt-image | 0,50 $ | 90 % |
| gpt-5.2 (2 llamadas) | 0,04 $ | 7 % |
| Todo el resto de texto | 0,008 $ | 1,5 % |

Cada imagen consumía exactamente **6.240 tokens de salida**, que según la tabla de
`PricingCatalog` es `high-1024x1536` = **0,250 $/imagen**. Era sistemático en las
76 imágenes del mes: no había ni una sola generada en otro tramo.

## Causa raíz

Cuatro problemas encadenados, todos de coste:

### 1. Era imposible elegir otra calidad desde la UI

El selector de calidad ofrecía solo `standard` y `hd`, que son los valores de
**DALL-E**. gpt-image usa `low` / `medium` / `high` / `auto`. Además:

- El valor por defecto era `standard` y el guardado hacía
  `if (_editImageQuality != "standard")`, así que **la clave `quality` nunca se
  escribía** en la configuración.
- Sin `quality` en la petición, el provider no seteaba `options.Quality` y la API
  aplicaba su default `auto`, que **resuelve a `high`**.
- Y si elegías `hd`, el `switch` del provider también lo mapeaba a `High`.

Resultado: los dos valores posibles de la UI acababan en `high`.

La ruta de **edición** de imagen (`/v1/images/edits`) tenía el mismo problema por
partida doble: `ImageEditOptions` solo recibía `Size`, nunca `Quality`.

### 2. El contexto del proyecto viajaba dos veces en el planner

`PromptPlannerService` metía `project.Context` dentro del prompt (vía
`BuildPlannerPrompt`) **y además** lo pasaba como `ProjectContext`, que el provider
vuelve a inyectar en el system prompt. La llamada diaria gastaba ~39.000 tokens de
entrada para ~450 de salida, la mitad texto duplicado.

### 3. El orden del system prompt rompía el caché de prompt

Los cuatro providers construían el system prompt con el prompt del módulo en
**segunda posición**, por delante de las reglas, el contexto del proyecto y el
historial. Como OpenAI y xAI cachean por *prefijo común*, el único bloque
compartido entre módulos de una misma ejecución eran las reglas obligatorias: todo
lo demás se pagaba entero en cada módulo. En los datos, solo el **5,6 %** de los
tokens de entrada llegaban cacheados.

### 4. `reasoning_effort` no existía en el código

Los gpt-5.x usaban el default del proveedor (`medium`). Los tokens de razonamiento
se facturan como salida: gpt-5 promediaba 5.201 tokens de salida por petición a
10 $/1M.

Como efecto colateral, el estimador de coste de la UI mentía: `EstimateImageCost`
asumía tamaño cuadrado cuando `auto` devuelve retrato, y traducía `standard`/`auto`
distinto a como lo hacía la API.

## Solución

**Calidad de imagen** — `Server/Services/Ai/GptImageOptions.cs` (nuevo) centraliza
la normalización y el default (`medium`). El provider la envía **siempre**, en las
tres rutas (generación, edición vía SDK y edición vía HTTP directo). `standard` y
`auto` se resuelven a `medium` en vez de a `high`. Esto arregla también los módulos
ya guardados, sin tocar datos.

En cliente, `Client/Models/AiModuleOptions.cs` (nuevo) expone las opciones reales
por familia de modelo, de modo que la página de Módulos y el editor del pipeline
no puedan desincronizarse. El guardado escribe la clave siempre.

**Contexto duplicado** — `PromptPlannerService` ya no pasa `ProjectContext`; el
contexto sigue viajando una sola vez, embebido en el prompt.

**Orden del system prompt** — `Server/Services/Ai/SystemPromptComposer.cs` (nuevo)
sustituye el bloque duplicado en los cuatro providers y ordena de más estable a
menos: reglas de formato → reglas obligatorias → contexto del proyecto → historial
→ prompt del módulo → aprendizaje. Los módulos de una misma ejecución comparten
prefijo cacheable. El prompt del usuario baja de posición pero conserva su etiqueta
de directiva prioritaria y queda más cerca del mensaje de usuario.

**Esfuerzo de razonamiento** — nueva clave de configuración `reasoningEffort`,
expuesta en la UI solo para modelos que la aceptan (gpt-5.x y serie o, excluyendo
`gpt-5-chat`). Si no se configura, no se envía y decide el proveedor.

Además, `OpenAiProvider` ahora devuelve `cachedInputTokens` y `reasoningTokens` en
los metadatos del paso, que son los dos números necesarios para verificar que todo
esto funciona.

## Impacto esperado

| Escenario | $/imagen | $/mes | Ahorro |
|-----------|----------|-------|--------|
| Antes: `high` 1024x1536 | 0,250 | ~16,3 | — |
| Ahora (default `medium`) | 0,063 | ~5,1 | **−69 %** |
| `medium` + gpt-image-1-mini | 0,0225 | ~2,7 | −83 % |
| `low` + mini | 0,0075 | ~1,8 | −89 % |

Los otros tres arreglos suman ~1-2 $/mes adicionales, pero evitan que el gasto se
dispare al cambiar de modelo: la llamada duplicada del planner pasa de 0,006 $ a
0,10 $ (17x) con solo elegir `gpt-4o` en el desplegable.

## Verificación

- `Server.Tests/OptimizacionTokens/` cubre la normalización de calidad y tamaño,
  el orden del system prompt (incluido que dos módulos compartan prefijo byte a
  byte) y la coherencia del estimador de coste con lo que se factura.
- En una ejecución real, los metadatos del paso deben mostrar
  `"quality":"medium"` en el payload auditado y ~1.584 tokens de salida por imagen
  en lugar de 6.240.

> **Nota de mantenimiento**: el vocabulario de calidad depende de la familia del
> modelo. Si se añade un proveedor de imagen nuevo, revisa
> `GptImageOptions` (servidor) y `AiModuleOptions` (cliente) a la vez: están
> pensados para no divergir.
