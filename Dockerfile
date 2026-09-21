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

# Sello del build. Va aqui, en la ultima capa y no junto al publish, porque esa
# capa se cachea y el pie de la aplicacion acababa ensenando el commit y la hora
# de un build anterior. La fecha se guarda en UTC (ISO-8601); el servidor la pasa
# a hora de Madrid al servirla.
ARG GIT_COMMIT=unknown
ARG BUILD_DATE
RUN echo "{\"commitHash\":\"${GIT_COMMIT}\",\"buildDate\":\"${BUILD_DATE:-$(date -u '+%Y-%m-%dT%H:%M:%SZ')}\"}" > /app/build-info.json

# Expose port 80 (nginx handles everything)
EXPOSE 80

ENTRYPOINT ["/entrypoint.sh"]
