# Fix: las casillas del inspector no se aplicaban ("Publicar" dejaba el artículo en borrador)

> Estado: **resuelto** · Fecha: 2026-08-04 · Área: `Server/Services/Ai/Handlers/IModuleHandler.cs`,
> `Server/Services/Ai/Handlers/ShopifyBlogModuleHandler.cs`, `Client/Components/Pipeline/PipelineCanvas.razor`

## Problema

El nodo **ShopifyBlog** guardaba siempre el artículo como **borrador**, incluso con la
casilla *"Publicar"* marcada en el inspector.

## Causa raíz

El inspector guarda las casillas como **cadena** (`SetModuleConfig(..., "true")`), así que
en la config del nodo quedan `"published": "true"` / `"published": "false"`. Al ejecutar,
esos valores llegan como `JsonElement` de tipo **String**, pero `GetConfigBool` solo
aceptaba el literal booleano de JSON:

```csharp
if (val is JsonElement je)
    return je.ValueKind == JsonValueKind.True;   // "true" (cadena) -> false
```

Una casilla marcada se leía como `false`, y además se ignoraba el valor por defecto
(`fallback`) porque el `return` era incondicional para cualquier `JsonElement`.

Afectaba a todas las casillas leídas con `GetConfigBool`, no solo a la de Shopify
(también `waitForResponse` del módulo de Interacción).

## Solución implementada

### 1. Interpretar bien el valor guardado

`ModuleExecutionContext.ParseConfigBool` cubre ahora los tipos con los que puede llegar un
flag: booleano JSON, **cadena** `"true"`/`"false"` (lo que escribe el editor), número
(`0` = false) y, para cualquier otra cosa, el valor por defecto del llamante.

### 2. ShopifyBlog publica visible por defecto

`ctx.GetConfigBool("published", true)`: sin tocar nada, el artículo sale **visible en la
tienda**. Para dejarlo en borrador hay que desmarcar *"Publicar"* en el nodo. La casilla
del inspector arranca marcada, en coherencia con ese valor por defecto.

## Cómo verificar el fix

1. Ejecuta un pipeline con un nodo ShopifyBlog **sin tocar** la casilla: el log debe decir
   `Publicando articulo en Shopify (...) — "..." (publicado)` y el artículo aparece visible.
2. Desmarca *"Publicar"*, vuelve a ejecutar: el log dice `(borrador)` y el artículo queda
   sin publicar. Antes de este fix, marcar la casilla no cambiaba nada.

Tests: `Server.Tests/ShopifyBlogHtml/ConfigBoolTests.cs`.
