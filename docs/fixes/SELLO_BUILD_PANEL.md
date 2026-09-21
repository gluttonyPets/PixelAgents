# Fix: el pie del panel mostraba un commit y una hora que no correspondian

> Estado: **resuelto en codigo, pendiente de reconstruir la imagen** · Area:
> `Client/Layout/NavMenu.razor`, `Server/Services/BuildInfo.cs`, `Dockerfile`

## Problema

El pie del panel izquierdo mostraba `b66fe06` cuando el commit desplegado era
otro. Ademas el SHA aparecia cortado y la hora estaba en UTC, no en horario de
Madrid.

### Causa raiz

Son tres problemas encadenados, y el tercero es el que hacia que los arreglos
del codigo no se notaran:

1. **Maquetacion**: commit y fecha se pintaban en una sola linea dentro de una
   barra de 220px, asi que el SHA se salia del panel.
2. **Zona horaria**: el `Dockerfile` sellaba `date -u`, y el cliente lo pintaba
   tal cual.
3. **El contenedor no se reconstruia**: `tools/deploy_develop.sh` (el que dispara
   `pixelagents-deploy.timer` cada minuto) hace `git merge --ff-only` y reinicia
   el worker de Leantime y el servidor de logs, pero **no toca Docker**. El
   codigo nuevo llegaba al servidor y la aplicacion seguia ejecutando una imagen
   vieja, con el `build-info.json` de cuando se construyo. De ahi un SHA que ya
   no existe en el repositorio.

## Solucion implementada

- El sello se calcula **dentro del build**, leyendo `.git/HEAD` y `refs/` del
  contexto (`Dockerfile`). Ya no depende de que nadie exporte `GIT_COMMIT` al
  desplegar. `.dockerignore` deja pasar solo esos ficheros, no los objetos.
- El `build-info.json` se escribe en la **ultima capa** de la imagen: la del
  `dotnet publish` se cachea y congelaba el sello.
- `Server/Services/BuildInfo.cs` acorta el SHA a 7 caracteres y convierte la
  fecha (UTC, ISO-8601) a horario de Madrid. **Manda el fichero de la imagen**
  sobre las variables de entorno: una variable pegada en un `.env` describia
  codigo que no se estaba ejecutando.
- `/api/build-info` devuelve tambien el hash entero, el origen del sello y la
  **fecha del binario en ejecucion**, que no la sella nadie y delata un
  contenedor sin reconstruir. El tooltip del pie los muestra.
- `tools/diagnostico_sello.sh` enseña de golpe los cuatro sitios de los que
  puede venir el valor.

## Como comprobarlo

El sello solo cambia al **reconstruir la imagen**; un `git pull` no basta:

```bash
./deploy.sh              # o: docker compose build --no-cache && docker compose up -d
./tools/diagnostico_sello.sh
```

## Pendiente

El auto-deploy sigue sin reconstruir el contenedor de la aplicacion: los cambios
de `Client/` y `Server/` no llegan a produccion hasta que alguien ejecuta
`deploy.sh` a mano. Es una decision de infraestructura, no se ha tocado.
