# Fix: "Handle has already been taken" al publicar en Shopify

> Estado: **resuelto** · Fecha: 2026-09-05 · Área: `Server/Services/Shopify/ShopifyService.cs`, `Server/Services/Shopify/ShopifyHandle.cs`, `Server/Services/Ai/Handlers/ShopifyBlogModuleHandler.cs`

## Problema

El módulo **ShopifyBlog** fallaba y no publicaba nada:

```
✗ Shopify Blog
Shopify rechazo el articulo: Handle has already been taken
```

## Causa raíz

El `handle` es el identificador de la URL del artículo (`/blogs/{blog}/{handle}`) y
Shopify obliga a que sea **único**. Cuando ya existe un artículo con ese identificador,
`articleCreate` devuelve el `userError`:

```json
{ "field": ["handle"], "message": "Handle has already been taken" }
```

Ocurre en dos situaciones, ambas normales en un pipeline que se ejecuta de forma
recurrente:

- El nodo (o el JSON estructurado del módulo anterior) trae un `handle`/`slug` fijo y se
  reutiliza en cada ejecución.
- No se manda `handle` y Shopify lo genera a partir del título: si la IA repite título
  (o se relanza la misma ejecución), el slug generado choca con el artículo anterior.

El servicio trataba ese `userError` como cualquier otro error fatal, así que **se perdía
el artículo entero** por un choque de slug, aunque el contenido fuese válido.

## Solución implementada

### 1. Reintento con sufijo (igual que hace el admin de Shopify)

`ShopifyService.CreateArticleAsync` distingue ahora el choque de handle del resto de
errores y reintenta la mutación con un identificador alternativo, hasta
`MaxHandleAttempts` (5) intentos en total:

| Intento | Handle |
|---------|--------|
| 1 | el pedido (o el que genera Shopify desde el título) |
| 2-4 | `mi-articulo-2`, `mi-articulo-3`, `mi-articulo-4` |
| 5 | `mi-articulo-20260905123000` (fecha UTC, corta cadenas largas de colisiones) |

El handle base sale del `handle` recibido o, si no llega ninguno, del **título**
normalizado. Si el artículo acaba publicándose con otro identificador, se registra un
aviso en el log de la ejecución (`ShopifyArticleResult.Warning`), no un fallo:

```
El identificador URL 'mi-articulo' ya estaba en uso en la tienda; el articulo se publico como 'mi-articulo-2'.
```

Si aun así se agotan los intentos, el error final explica qué hay que tocar
(el título o el campo *Identificador URL* del nodo) en lugar de dejar el mensaje crudo
de Shopify.

### 2. Lógica de handles en un único sitio

`Server/Services/Shopify/ShopifyHandle.cs` concentra:

- `Slugify`: normaliza acentos y separadores y recorta a `MaxLength` (120).
- `Candidate` / `WithSuffix`: generan la alternativa recortando la base para no pasarse
  del máximo.
- `IsTakenError`: detecta el `userError` de handle duplicado (Shopify no expone un código
  estable, así que se miran campo y mensaje).

El `Slugify` privado que tenía `ShopifyBlogModuleHandler` se ha eliminado: el handler usa
el compartido.

### 3. Lectura completa de `userErrors`

`ShopifyService.ReadUserError` aplana el campo `field` (que Shopify manda como lista,
p. ej. `["article", "handle"]`) junto al mensaje, para poder clasificar el error.

## Cómo verificar el fix

1. Ejecuta dos veces seguidas un pipeline con el mismo título / identificador URL.
2. El segundo artículo **se publica igualmente**, con handle `...-2`, y el log muestra el
   aviso del identificador renombrado.
3. Tests: `Server.Tests/ShopifyBlogHtml/ShopifyHandleTests.cs` (normalización del slug,
   sufijos numéricos y por fecha, recorte de longitud y detección del `userError`).
