# Fix: la imagen destacada de Shopify fallaba con "Invalid URL provided"

> Estado: **resuelto** · Fecha: 2026-08-03 · Área: `Server/Services/Shopify/ShopifyService.cs`, `Server/Services/Ai/Handlers/ShopifyBlogModuleHandler.cs`, `Server/Program.cs`

## Problema

Al conectar un módulo de imagen al puerto `input_image` del módulo **ShopifyBlog**, la
publicación fallaba y Shopify rechazaba el artículo entero:

```
Shopify rechazo el articulo: Image upload failed. Invalid URL provided.
```

## Causa raíz

El handler adjuntaba la imagen destacada pasando a `articleCreate` la **URL pública de
nuestro servidor** (`image.url`), y **Shopify descarga esa URL desde sus servidores**.

La URL se construye como `{PublicBaseUrl}{ruta}`, y `PublicBaseUrl` sale de `BaseUrl` /
`AllowedOrigin`. En `docker-compose.yml` el valor por defecto es:

```yaml
BaseUrl: "http://${PUBLIC_IP:-localhost}:${APP_PORT:-8080}"
```

Es decir: **`http://<IP-pública>:8080`** — HTTP plano, IP desnuda y puerto no estándar.
Shopify no acepta ese tipo de URL para descargar una imagen y responde *"Image upload
failed. Invalid URL provided."*, tirando con ella **todo el artículo**. Con `BaseUrl` sin
configurar el problema es aún más directo: la URL sale **relativa** (`/api/public/...`).

El planteamiento de fondo era frágil: hacía depender la publicación de que nuestro
servidor fuese accesible desde internet con un certificado y un dominio válidos.

## Solución implementada

### 1. Subir la imagen directamente a Shopify (staged upload) — vía principal

`ShopifyService.StageImageUploadAsync` implementa el flujo estándar de Shopify:

1. `stagedUploadsCreate` (`resource: IMAGE`, `httpMethod: POST`) → devuelve `url`,
   `resourceUrl` y `parameters` firmados.
2. Se sube el fichero por `multipart/form-data` al destino, con los parámetros firmados
   **antes** del campo `file` (si se altera el orden, el destino responde
   `SignatureDoesNotMatch`).
3. Se pasa la `resourceUrl` como `image.url` de `articleCreate`.

Así **Shopify ya no descarga nada de nuestro servidor**: funciona aunque esté en
`http://IP:8080`, detrás de un firewall o sin dominio público.

> `fileSize` se omite a propósito: solo es obligatorio para `VIDEO` y `MODEL_3D`, y al
> enviarlo el destino firma una política con `content-length-range` que puede provocar
> `SignatureDoesNotMatch`.

### 2. Nunca perder el artículo por culpa de la imagen

Si `articleCreate` devuelve `userErrors` que solo afectan a la imagen, se reintenta
**sin imagen destacada** y se registra un aviso (`ShopifyArticleResult.Warning`), en vez
de dar el artículo por perdido. Lo mismo si falla el staged upload.

### 3. Respaldo por URL: solo si Shopify puede descargarla

`IsPubliclyReachableImageUrl` (en el handler) exige **https absoluto con host público**:
descarta URLs relativas, `http` plano, `localhost`, loopback, redes privadas
(`10/8`, `172.16/12`, `192.168/16`), link-local y hosts sin dominio.

### 4. Soporte `HEAD` en el endpoint público

`/api/public/files/{tenant}/{executionId}/{fileId}/{fileName}` pasó de `MapGet` a
`MapMethods(["GET", "HEAD"])` y sirve con `Content-Disposition: inline`, porque los
validadores de URL hacen primero un `HEAD` (mismo patrón que
[`BUFFER_HEAD_REQUEST.md`](BUFFER_HEAD_REQUEST.md)).

## Requisito de scopes

Para adjuntar imágenes, la app de Shopify necesita, además de `read_content` y
`write_content`, el scope **`write_files`** (lo usa `stagedUploadsCreate`). Tras añadirlo
hay que **reinstalar** la app. Si falta, el log lo indica explícitamente y el artículo se
publica sin imagen.

## Cómo verificar el fix

1. Conecta un módulo de imagen al puerto `input_image` del nodo ShopifyBlog y ejecuta.
2. En el log de la ejecución debe aparecer:
   `Imagen destacada: output.png (image/png, N bytes) se subira directamente a Shopify.`
3. El artículo se crea en Shopify **con** la imagen destacada, sin depender de `BaseUrl`.
4. Si falta `write_files`, el artículo se publica igualmente y el log avisa de qué scope
   añadir, en lugar de fallar con *"Invalid URL provided"*.

Tests: `Server.Tests/ShopifyBlogHtml/ShopifyImageUrlTests.cs` cubre el filtro de URLs de
respaldo (incluido el caso `http://IP:8080` del docker-compose).
