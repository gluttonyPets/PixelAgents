# Configuración de Buffer para Publicación en Redes Sociales

## Solución Implementada: Pool de URLs Permanentes

PixelAgents ahora utiliza un **pool de 10 URLs permanentes** para las imágenes de Buffer:

- **URLs fijas**: `http://TU_IP:8080/api/public/buffer-image/0` hasta `/9`
- **Sistema circular FIFO**: Las 10 imágenes más recientes siempre están disponibles
- **Sin autenticación**: Buffer puede acceder libremente a estas URLs
- **Persistencia**: Las imágenes permanecen disponibles incluso después de reiniciar el servidor

### ¿Cómo funciona?

1. Cuando un pipeline genera una imagen para publicar en Buffer
2. La imagen se copia al siguiente slot disponible (0-9, rotan circularmente)
3. Buffer recibe la URL permanente (ej: `http://51.75.26.236:8080/api/public/buffer-image/3`)
4. Buffer puede descargar la imagen cuando la necesite (inmediatamente o más tarde)
5. La imagen permanece disponible hasta que sea reemplazada por una nueva (después de 10 publicaciones)

### Verificar el estado del pool

Puedes ver qué imágenes están actualmente en el pool:

```bash
curl -X GET "http://TU_IP:8080/api/buffer-pool/status" \
  -H "Cookie: tu-cookie-de-sesion"
```

O desde el navegador (autenticado): `http://TU_IP:8080/api/buffer-pool/status`

## Problema Original (Ya Resuelto)

Antes de esta implementación, cuando intentabas publicar en Instagram o TikTok usando Buffer, la publicación fallaba con errores como:

```
"An unknown error has occurred, please get in touch if this persists."
```

Esto ocurría porque **Buffer no podía acceder a las imágenes desde internet** debido a URLs dinámicas que expiraban o no eran accesibles.

## Causa del Problema

PixelAgents genera archivos (imágenes, videos) y los expone a través de URLs públicas para que Buffer pueda descargarlos. Por ejemplo:

```
http://TU_IP_PUBLICA:8080/api/public/files/tenant123/execution-id/file-id/imagen.png
```

Si la URL contiene `localhost` o `127.0.0.1`, Buffer no puede acceder a ella desde internet y la publicación falla.

## Solución Paso a Paso

### 1. Obtén tu IP Pública o Dominio

#### Opción A: IP Pública

Si tu servidor tiene una IP pública, obtenla con:

```bash
curl ifconfig.me
```

Por ejemplo: `123.45.67.89`

#### Opción B: Dominio

Si tienes un dominio (ej: `miagencia.com`), asegúrate de que:
- El dominio apunte a tu servidor (registro DNS tipo A)
- El puerto 8080 (o el que uses) esté abierto en tu firewall

### 2. Configura las Variables de Entorno

#### Usando archivo .env (Recomendado)

1. Crea un archivo `.env` en la raíz del proyecto (si no existe):

```bash
cd /ruta/a/PixelAgents
cp .env.example .env
```

2. Edita `.env` y configura tu IP pública o dominio:

```bash
# Con IP pública
PUBLIC_IP=123.45.67.89

# O con dominio
PUBLIC_IP=miagencia.com

# Otros valores importantes
APP_PORT=8080
POSTGRES_PASSWORD=tu_password_seguro_aqui
```

3. Guarda el archivo.

#### Sin archivo .env (Variables directas)

Si prefieres no usar .env, puedes exportar las variables antes de ejecutar docker compose:

```bash
export PUBLIC_IP=123.45.67.89
export APP_PORT=8080
export POSTGRES_PASSWORD=tu_password_aqui
```

### 3. Reinicia los Contenedores

Una vez configuradas las variables, reinicia la aplicación:

```bash
# Si usas el script de deploy (carga .env automáticamente)
./deploy.sh

# O manualmente con docker compose
docker compose down
docker compose up -d --build
```

### 4. Verifica la Configuración

1. Revisa los logs para confirmar que la URL pública está correcta:

```bash
docker compose logs pixelagents | grep BaseUrl
```

2. Cuando ejecutes un pipeline que publique en Buffer, verifica en los logs que las URLs generadas son públicas:

```
Logs de ejecución > Ver detalles > Buscar "Buffer API Request"
```

Deberías ver URLs como:
```
http://123.45.67.89:8080/api/public/files/...
```

NO como:
```
http://localhost:8080/api/public/files/...
```

### 5. Abre el Puerto en el Firewall

Asegúrate de que el puerto de la aplicación (por defecto 8080) esté abierto en tu firewall para conexiones HTTP entrantes.

#### En Ubuntu/Debian con UFW:

```bash
sudo ufw allow 8080/tcp
```

#### En otros sistemas:

Consulta la documentación de tu firewall.

## Verificación Final

Para verificar que todo funciona:

1. Abre un navegador desde **otro dispositivo** (no el servidor)
2. Ve a: `http://TU_IP_PUBLICA:8080`
3. Si la aplicación carga, la configuración es correcta

También puedes probar acceder directamente a un archivo público (copia una URL del log de ejecución y ábrela en el navegador).

## Troubleshooting

### Buffer sigue fallando después de configurar PUBLIC_IP

**Verifica que:**

1. El puerto esté abierto en el firewall
2. Tu router no esté bloqueando el puerto (si estás detrás de NAT, necesitas port forwarding)
3. No estés usando HTTPS en la configuración (a menos que tengas certificado SSL configurado)
4. La URL pública sea accesible desde internet (prueba desde tu móvil con datos, no WiFi)

### La URL en los logs sigue mostrando localhost

Esto significa que las variables de entorno no se aplicaron correctamente:

1. Verifica que `.env` existe y contiene `PUBLIC_IP=tu-ip-aqui`
2. Reinicia completamente los contenedores:
   ```bash
   docker compose down
   docker compose up -d
   ```
3. Verifica las variables de entorno del contenedor:
   ```bash
   docker compose exec pixelagents env | grep -E "BaseUrl|AllowedOrigin"
   ```

### Estoy detrás de un router/NAT

Si tu servidor está en una red privada detrás de un router:

1. Obtén la IP pública de tu router: `curl ifconfig.me`
2. Configura **port forwarding** en tu router:
   - Puerto externo: 8080
   - Puerto interno: 8080
   - IP interna: IP de tu servidor en la red local
3. Usa la IP pública del router en `PUBLIC_IP`

## Consideraciones de Seguridad

### Exponer el servidor a internet

Al configurar una IP pública, tu servidor será accesible desde internet. Considera:

1. **Usar contraseñas fuertes** en todas las cuentas
2. **Mantener el sistema actualizado**
3. **Usar HTTPS con certificado SSL** (Let's Encrypt) para producción
4. **Configurar un firewall** que solo permita los puertos necesarios

### Para producción con HTTPS

Si tienes un dominio y quieres usar HTTPS:

1. Obtén un certificado SSL (Let's Encrypt con Certbot)
2. Configura nginx o Caddy como reverse proxy con SSL
3. Actualiza las variables en `.env`:
   ```bash
   PUBLIC_IP=midominio.com
   APP_PORT=443
   ```
4. Actualiza los comentarios en `docker-compose.yml` para usar HTTPS

## Efecto Secundario: Telegram Webhook

**⚠️ IMPORTANTE:** Si también usas Telegram para interacciones, después de cambiar `PUBLIC_IP` necesitas actualizar el webhook de Telegram.

### Síntomas

- El servidor envía mensajes a Telegram correctamente
- Cuando presionas botones en Telegram, el servidor NO recibe la respuesta
- El pipeline se queda en "WaitingForInput"

### Solución

Usa el script de diagnóstico:

```bash
cd /ruta/a/PixelAgents
./tools/fix_telegram_webhook.sh TU_BOT_TOKEN
```

O manualmente desde la interfaz de PixelAgents:

1. Ve a la sección **Mensajería** (`/mensajeria`) y abre tu conexión de Telegram
2. Simplemente **guarda la conexión de nuevo** (sin cambiar nada)
3. Esto re-registrará el webhook con la nueva URL pública

## Soporte Adicional

Si después de seguir estos pasos Buffer sigue fallando:

1. Revisa los logs completos de la ejecución en PixelAgents
2. Verifica en la interfaz de Buffer si el post se creó pero falló al publicar
3. Comprueba que Buffer tenga acceso a la URL pública (prueba la URL en un navegador externo)
4. Contacta al soporte de Buffer con el error específico y la URL que está intentando acceder
