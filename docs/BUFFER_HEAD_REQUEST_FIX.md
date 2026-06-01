# Fix: Soporte para solicitudes HEAD en endpoint de Buffer

## Problema

Buffer estaba rechazando las URLs de imágenes con error HTTP 405 ("Método no permitido"). 

### Causa raíz

El endpoint `/api/public/buffer-image/{slot}` solo aceptaba solicitudes GET, pero Buffer (y muchos validadores de URL) envían primero una solicitud **HEAD** ligera para verificar que la URL es válida antes de descargar la imagen completa.

```
Flujo de Buffer:
1. Buffer recibe URL de imagen
2. Envía HEAD request para validar → ❌ 405 Method Not Allowed
3. No puede continuar con la publicación
```

## Solución implementada

Se actualizó el endpoint para aceptar tanto GET como HEAD:

### Cambio en `Server/Program.cs` (línea 2038):

**Antes:**
```csharp
app.MapGet("/api/public/buffer-image/{slot:int}", async (...)
```

**Después:**
```csharp
app.MapMethods("/api/public/buffer-image/{slot:int}", new[] { "GET", "HEAD" }, async (...)
```

### Comportamiento

- **GET**: Devuelve la imagen completa (como antes)
- **HEAD**: Devuelve los mismos headers (Content-Type, Content-Disposition, etc.) pero SIN el cuerpo de la imagen

ASP.NET Core maneja esto automáticamente - ejecuta el mismo handler pero omite el cuerpo en respuestas HEAD.

## Cómo verificar el fix

### Opción 1: Usar el script de prueba

```bash
./test_buffer_head_request.sh https://tu-dominio.com 1
```

### Opción 2: Probar manualmente con curl

```bash
# Prueba HEAD (lo que Buffer usa)
curl -I -X HEAD https://tu-dominio.com/api/public/buffer-image/1

# Debería devolver:
# HTTP/1.1 200 OK
# Content-Type: image/jpeg
# Content-Disposition: inline
# Content-Length: XXXXX
```

### Opción 3: Probar directamente con Buffer

1. Despliega los cambios
2. Crea un nuevo post en Buffer con una imagen
3. Buffer ahora debería aceptar la URL sin errores

## Verificación de éxito

✅ El endpoint devuelve **200 OK** para solicitudes HEAD  
✅ El endpoint devuelve **200 OK** para solicitudes GET  
✅ Buffer acepta la URL sin errores 405  
✅ Las imágenes se publican correctamente en redes sociales

## Notas adicionales

### ¿Por qué HEAD?

Los validadores de URL (Buffer, Twitter, Facebook, etc.) usan HEAD porque:
- Es más rápido (no descarga el contenido completo)
- Usa menos ancho de banda
- Solo necesitan verificar que la URL existe y el tipo de contenido

### Estándar HTTP

Según el RFC 7231, cualquier endpoint que soporte GET **debería** también soportar HEAD. Es una buena práctica implementar ambos.

### Alternativas consideradas

El soporte de Buffer sugirió dos opciones:
1. ✅ **Implementar HEAD en el endpoint** (solución elegida - más simple)
2. ⚠️  Migrar a almacenamiento estático (S3, R2, Cloudinary) - innecesario ahora

## Próximos pasos

1. Compilar el proyecto
2. Desplegar a producción/staging
3. Ejecutar el script de prueba
4. Verificar con Buffer que las publicaciones funcionan
5. Monitorear logs para confirmar que Buffer usa HEAD correctamente
