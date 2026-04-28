# Leantime MCP Setup

## Objetivo

Sustituir la automatización basada en `claude_agent_sdk` de Python por:

- Claude CLI en modo no interactivo
- MCP oficial de Leantime
- worker Node.js para polling y reanudación de sesiones

## Componentes

- Worker: [automation/leantime_mcp_worker.js](/C:/Proyectos/PixelAgents/automation/leantime_mcp_worker.js)
- MCP config: [.mcp.json](/C:/Proyectos/PixelAgents/.mcp.json)
- Wrapper del bridge oficial: [tools/run_leantime_mcp.sh](/C:/Proyectos/PixelAgents/tools/run_leantime_mcp.sh)
- Instalación systemd: [tools/install_pixelagents.sh](/C:/Proyectos/PixelAgents/tools/install_pixelagents.sh)

## Requisitos en el servidor

Instalar Claude CLI y el bridge oficial:

```bash
npm install -g @anthropic-ai/claude-code leantime-mcp
```

Claude CLI debe quedar autenticado para el usuario que ejecuta el servicio.

## Variables necesarias en `~/.config/pixelagents/leantime.env`

```bash
LEANTIME_API_URL=https://tu-leantime/api/jsonrpc
LEANTIME_API_KEY=lt_xxx
LEANTIME_PROJECT_ID=3

READY_STATUS_ID=1
IN_PROGRESS_STATUS_ID=2
REVIEW_STATUS_ID=3

LEANTIME_MCP_URL=https://tu-leantime/mcp
LEANTIME_MCP_TOKEN=pat_o_token_recomendado
```

Notas:

- `LEANTIME_MCP_TOKEN` debería ser un Personal Access Token si queréis una identidad de usuario real en comentarios y acciones personalizadas.
- Si no se define `LEANTIME_MCP_TOKEN`, el wrapper usa `LEANTIME_API_KEY` como fallback.

## Comandos útiles

```bash
sudo systemctl restart pixelagents-leantime-worker.service
sudo systemctl status pixelagents-leantime-worker.service
journalctl -u pixelagents-leantime-worker -f
```

## Diseño operativo

- El worker hace polling ligero vía JSON-RPC para detectar tareas en `Ready` y `Review`.
- Toda interacción funcional del agente con Leantime debe pasar por el MCP oficial.
- La descripción original de la tarea es inmutable.
- Los comentarios automáticos en Discusión deben empezar por `[CLAUDE_AUTOMATION]`.
