# ── Stage 1: Build Server ──
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-server
WORKDIR /src

RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*

COPY .git/ .git/
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
RUN apt-get update && apt-get install -y nginx && rm -rf /var/lib/apt/lists/*

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

# Expose port 80 (nginx handles everything)
EXPOSE 80

ENTRYPOINT ["/entrypoint.sh"]
