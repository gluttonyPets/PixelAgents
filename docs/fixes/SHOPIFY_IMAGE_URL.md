# Fix: la imagen destacada de Shopify fallaba con "Invalid URL provided"

> Estado: **resuelto** · Fecha: 2026-07-26 · Área: `Server/Services/Ai/Handlers/ShopifyBlogModuleHandler.cs`, `Server/Program.cs` (`/api/public/files/...`)

## Problema

Al conectar un módulo de imagen al puerto `input_image` del módulo **ShopifyBlog**, la
publicación fallaba y Shopify rechazaba el artículo entero:

```
Shopify rechazo el articulo: Image upload failed. Invalid URL provided.
```

## Causa raíz

El handler adjunta la imagen destacada pasando a la mutación `articleCreate` la URL
pública del archivo (`image: { url }`); Shopify **descarga esa URL desde sus servidores**
al crear el artículo. La URL se construye en `ModuleExecutionContext.GetPublicFileUrl`
como `{PublicBaseUrl}{ruta}`, donde `PublicBaseUrl` sale de la config `BaseUrl` /
`AllowedOrigin`.

Dos formas de generar una URL que Shopify no puede descargar:

1. **URL relativa** — si `BaseUrl`/`AllowedOrigin` no están configuradas, `PublicBaseUrl`
   queda vacío y `GetPublicFileUrl` devuelve solo la ruta (`/api/public/files/...`). El
   guard antiguo solo comprobaba `!IsNullOrWhiteSpace`, así que una ruta relativa pasaba
   el filtro y se enviaba a Shopify → *"Invalid URL provided"*.
2. **localhost / red privada** — con `BaseUrl` apuntando a `http://localhost:5000` (el
   valor por defecto de desarrollo) o a una IP privada, la URL es absoluta pero Shopify
   no la alcanza desde internet.

Además, el endpoint público de archivos solo aceptaba **GET**. Los validadores de URL
(Shopify, Buffer, etc.) hacen primero una petición **HEAD** para comprobar la URL antes
de descargarla; un endpoint solo-GET responde 405 y el validador la da por inválida
(mismo patrón que [`BUFFER_HEAD_REQUEST.md`](BUFFER_HEAD_REQUEST.md)).

## Solución implementada

### 1. Validar la URL antes de enviarla (`ShopifyBlogModuleHandler`)

Nuevo filtro `IsPubliclyReachableImageUrl`: solo adjunta la imagen si la URL es
**absoluta http/https** con un host accesible desde internet (descarta URLs relativas,
`localhost`, loopback, `0.0.0.0`, IPs privadas `10/8`, `172.16/12`, `192.168/16`,
link-local `169.254/16` y hostnames sin dominio público).

Si la URL no es utilizable, el artículo se publica **sin imagen destacada** y se registra
un aviso accionable, en vez de perder todo el artículo:

```
La imagen conectada genero una URL no accesible desde internet (<url>);
Shopify no podria descargarla, asi que el articulo se publicara SIN imagen destacada.
Configura 'BaseUrl' con la URL publica del servidor (https, no localhost) para adjuntar imagenes.
```

### 2. Soporte HEAD + `inline` en el endpoint público (`Server/Program.cs`)

`/api/public/files/{tenant}/{executionId}/{fileId}/{fileName}` pasó de `MapGet` a
`MapMethods(["GET", "HEAD"])` y ahora sirve el archivo con `Content-Disposition: inline`,
para que los validadores de URL acepten la imagen.

## Cómo verificar el fix

- Con `BaseUrl` **sin configurar** o en `localhost`: el artículo se publica igualmente
  (sin imagen) y aparece el aviso en el log de la ejecución, en lugar del error
  *"Invalid URL provided"* que antes tumbaba el artículo.
- Con `BaseUrl` = URL pública `https`: la imagen se adjunta y Shopify la re-hostea en su
  CDN.
- HEAD sobre el endpoint público:

  ```bash
  curl -I https://tu-dominio.com/api/public/files/<tenant>/<exec>/<file>/output.png
  # HTTP/1.1 200 OK · Content-Type: image/png · Content-Disposition: inline
  ```

- Tests: `Server.Tests/ShopifyBlogHtml/ShopifyImageUrlTests.cs` cubre el filtro de URLs.

## Requisito operativo

Para que Shopify (y Buffer/Telegram) puedan descargar los archivos generados, **`BaseUrl`
debe apuntar a la URL pública del servidor** (`https`, dominio o IP pública), no a
`localhost`.
