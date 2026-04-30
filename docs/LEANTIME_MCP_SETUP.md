# Leantime MCP Setup

## Objetivo

Usar la ruta actual recomendada para Claude + Leantime:

- Claude CLI en modo no interactivo
- MCP remoto HTTP de Leantime en `/mcp`
- worker Node.js para polling y reanudacion de sesiones

## Componentes

- Worker: [automation/leantime_mcp_worker.js](/C:/Proyectos/PixelAgents/automation/leantime_mcp_worker.js)
- MCP config: [.mcp.json](/C:/Proyectos/PixelAgents/.mcp.json)
- Instalacion systemd: [tools/install_pixelagents.sh](/C:/Proyectos/PixelAgents/tools/install_pixelagents.sh)
- Wrapper legacy opcional: [tools/run_leantime_mcp.sh](/C:/Proyectos/PixelAgents/tools/run_leantime_mcp.sh)

## Requisitos en el servidor

Instalar Claude CLI:

```bash
npm install -g @anthropic-ai/claude-code
```

Claude CLI debe quedar autenticado para el usuario que ejecuta el servicio.

Requisitos de Leantime:

- Plugin MCP Server habilitado en la instancia self-hosted.
- Personal Access Token recomendado para que Claude actue con identidad real.

## Variables necesarias en `~/.config/pixelagents/leantime.env`

```bash
LEANTIME_API_URL=https://tu-leantime/api/jsonrpc
LEANTIME_API_KEY=lt_xxx
LEANTIME_PROJECT_ID=3

READY_STATUS_ID=1
IN_PROGRESS_STATUS_ID=2
REVIEW_STATUS_ID=3

LEANTIME_MCP_URL=https://tu-leantime/mcp
LEANTIME_MCP_TOKEN=pat_recomendado
```

Notas:

- `.mcp.json` usa `LEANTIME_MCP_URL` y `LEANTIME_MCP_TOKEN` con transporte HTTP nativo de Claude Code.
- `LEANTIME_MCP_TOKEN` deberia ser un Personal Access Token. Las API keys clasicas de Leantime son cuentas de servicio y no preservan bien el contexto de "mis tareas" ni la autoria real.
- `LEANTIME_API_KEY` se mantiene para el polling JSON-RPC del worker, no para las mutaciones de Claude dentro del MCP.
- Si alguna instalacion antigua sigue necesitando el bridge `leantime-mcp`, el wrapper legacy se puede seguir usando manualmente, pero ya no es la ruta principal del proyecto.

## Comandos utiles

```bash
sudo systemctl restart pixelagents-leantime-worker.service
sudo systemctl status pixelagents-leantime-worker.service
journalctl -u pixelagents-leantime-worker -f
```

## Diseno operativo

- El worker hace polling ligero via JSON-RPC para detectar tareas en `Ready` y `Review`.
- Toda interaccion funcional del agente con Leantime debe pasar por el MCP oficial.
- La descripcion original de la tarea es inmutable.
- Los comentarios automaticos en Discusion deben empezar por `[CLAUDE_AUTOMATION]`.
- El worker persiste el `session_id` de Claude tambien en comentarios tecnicos de Leantime para poder retomar la conversacion tras reinicios.
