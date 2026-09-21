# ── Stage 1: Build Server ──
# trigger rebuild: 2026-09-10 (reglas: sub-pestanas Mis reglas / Constantes)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-server
WORKDIR /src

COPY Server/Server.csproj Server/
RUN dotnet restore Server/Server.csproj

COPY Server/ Server/
RUN dotnet publish Server/Server.csproj -c Release -o /app/server

# ── Stage 2: Build Client (Blazor WASM) ──
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-client
WORKDIR /src

COPY Client/Client.csproj Client/
RUN dotnet restore Client/Client.csproj

COPY Client/ Client/
RUN dotnet publish Client/Client.csproj -c Release -o /app/client

# ── Stage 3: Runtime ──
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Install nginx for serving Blazor WASM + reverse proxy to API
# ffmpeg lo usa el modulo de montaje de video (VideoAssembler) para unir los
# clips generados en un unico MP4. Sin el, ese modulo falla con un error explicito.
# tzdata hace falta para resolver zonas IANA ("Europe/Madrid"): la usan el
# planificador y la hora del build que se muestra en el pie del panel.
RUN apt-get update && apt-get install -y nginx ffmpeg tzdata && rm -rf /var/lib/apt/lists/*

# Copy server build
WORKDIR /app
COPY --from=build-server /app/server .

# Copy client build to nginx html
COPY --from=build-client /app/client/wwwroot /var/www/html

# Copy nginx config
COPY nginx.conf /etc/nginx/nginx.conf

# Copy startup script
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# ── Sello del build ────────────────────────────────────────────────────────
# El commit se resuelve leyendo .git del propio contexto de build: asi el sello
# describe el arbol de fuentes que se acaba de compilar, y no depende de que
# alguien exporte GIT_COMMIT al desplegar (que es como el pie acabo mostrando un
# hash que no existia en el repositorio). GIT_COMMIT queda de reserva por si el
# contexto no trae .git. Va en la ultima capa: la de `dotnet publish` se cachea y
# el sello se quedaba congelado en un build anterior.
#
# El `.dockerignore` deja pasar solo HEAD, packed-refs y refs/, no los objetos,
# asi que esto no engorda la imagen. `.dockerignore` va en la lista de origenes
# aposta: existe siempre, y evita que el COPY falle si .git no esta en el
# contexto (build desde un tarball, por ejemplo).
COPY .dockerignore .git/HEAD* .git/packed-refs* /gitmeta/
COPY .dockerignore .git/refs* /gitmeta/refs/

ARG GIT_COMMIT=unknown
ARG BUILD_DATE
RUN set -u; \
    sha=""; \
    if [ -f /gitmeta/HEAD ]; then \
        head_content=$(cat /gitmeta/HEAD 2>/dev/null || echo ""); \
        case "$head_content" in \
            "ref: "*) \
                ref=${head_content#ref: }; \
                for candidate in "/gitmeta/$ref" "/gitmeta/${ref#refs/}" "/gitmeta/refs/$ref"; do \
                    if [ -f "$candidate" ]; then sha=$(cat "$candidate" 2>/dev/null || echo ""); break; fi; \
                done; \
                if [ -z "$sha" ] && [ -f /gitmeta/packed-refs ]; then \
                    sha=$(awk -v r="$ref" '$2 == r { print $1; exit }' /gitmeta/packed-refs 2>/dev/null || echo ""); \
                fi; \
                ;; \
            *) sha=$head_content ;; \
        esac; \
    fi; \
    [ -n "$sha" ] || sha="${GIT_COMMIT}"; \
    echo "{\"commitHash\":\"${sha}\",\"buildDate\":\"${BUILD_DATE:-$(date -u '+%Y-%m-%dT%H:%M:%SZ')}\"}" > /app/build-info.json; \
    rm -rf /gitmeta; \
    cat /app/build-info.json

# Expose port 80 (nginx handles everything)
EXPOSE 80

ENTRYPOINT ["/entrypoint.sh"]
