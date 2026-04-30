#!/usr/bin/env bash
# Instalador idempotente de PixelAgents en una máquina Linux con systemd.
# Genera y activa:
#   - pixelagents-leantime-worker.service  (worker Node.js + Claude CLI + MCP oficial de Leantime)
#   - pixelagents-log-server.service       (HTTP server que sirve los logs)
#   - pixelagents-deploy.service + .timer  (auto-deploy al cambiar develop)
#
# Idempotente: re-ejecutarlo regenera las units y hace daemon-reload.
#
# Uso:
#   sudo ./tools/install_pixelagents.sh \
#        [--user debian] \
#        [--project-dir /home/debian/Proyectos/PixelAgents] \
#        [--branch develop] \
#        [--interval 1min] \
#        [--log-port 9000] \
#        [--no-enable]
#
# Si no eres root, se re-ejecuta con sudo.

set -euo pipefail

PA_USER=""
PA_PROJECT_DIR=""
DEPLOY_BRANCH="develop"
DEPLOY_INTERVAL="1min"
LOG_PORT="9000"
DO_ENABLE=1
WITH_SUDOERS=0

usage() {
  sed -n '2,20p' "$0"
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --user) PA_USER="$2"; shift 2 ;;
    --project-dir) PA_PROJECT_DIR="$2"; shift 2 ;;
    --branch) DEPLOY_BRANCH="$2"; shift 2 ;;
    --interval) DEPLOY_INTERVAL="$2"; shift 2 ;;
    --log-port) LOG_PORT="$2"; shift 2 ;;
    --no-enable) DO_ENABLE=0; shift ;;
    --with-sudoers) WITH_SUDOERS=1; shift ;;
    -h|--help) usage 0 ;;
    *) echo "Argumento desconocido: $1" >&2; usage 1 ;;
  esac
done

# Necesita root para systemd
if [ "$EUID" -ne 0 ]; then
  echo "[install] necesita root, re-ejecutando con sudo..."
  exec sudo -E "$0" "$@"
fi

# Defaults dinámicos
if [ -z "$PA_USER" ]; then
  PA_USER="${SUDO_USER:-$(logname 2>/dev/null || echo root)}"
fi

PA_HOME="$(getent passwd "$PA_USER" | cut -d: -f6 || true)"
[ -n "$PA_HOME" ] || { echo "[install][ERROR] el usuario $PA_USER no existe" >&2; exit 1; }

if [ -z "$PA_PROJECT_DIR" ]; then
  # Si el script se ejecuta dentro del repo, úsalo como default
  SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
  if [ -d "$SCRIPT_DIR/../.git" ]; then
    PA_PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
  else
    PA_PROJECT_DIR="$PA_HOME/Proyectos/PixelAgents"
  fi
fi

[ -d "$PA_PROJECT_DIR/.git" ] || {
  echo "[install][ERROR] $PA_PROJECT_DIR no es un repo git" >&2
  exit 1
}

CONFIG_DIR="$PA_HOME/.config/pixelagents"
ENV_FILE="$CONFIG_DIR/leantime.env"
WORKER_SCRIPT="$PA_PROJECT_DIR/automation/leantime_mcp_worker.js"
LOG_SERVER_SCRIPT="$PA_PROJECT_DIR/automation/log_server.py"
LOG_DIR="$PA_PROJECT_DIR/automation/logs"
DEPLOY_SCRIPT="$PA_PROJECT_DIR/tools/deploy_develop.sh"
LOG_SERVER_SCRIPT="$PA_PROJECT_DIR/tools/log_server.py"

WORKER_UNIT="/etc/systemd/system/pixelagents-leantime-worker.service"
LOG_UNIT="/etc/systemd/system/pixelagents-log-server.service"
DEPLOY_UNIT="/etc/systemd/system/pixelagents-deploy.service"
DEPLOY_TIMER="/etc/systemd/system/pixelagents-deploy.timer"

echo "[install] Usuario:        $PA_USER ($PA_HOME)"
echo "[install] Project dir:    $PA_PROJECT_DIR"
echo "[install] Rama deploy:    $DEPLOY_BRANCH"
echo "[install] Intervalo:      $DEPLOY_INTERVAL"
echo "[install] Puerto logs:    $LOG_PORT"

# Crear directorios necesarios con ownership correcto
install -d -o "$PA_USER" -g "$PA_USER" -m 0750 "$CONFIG_DIR"
install -d -o "$PA_USER" -g "$PA_USER" -m 0755 "$LOG_DIR"

# Avisos sin bloquear
[ -f "$ENV_FILE" ] || echo "[install][WARN] no existe $ENV_FILE; créalo con LEANTIME_API_URL, LEANTIME_API_KEY, LEANTIME_PROJECT_ID antes de arrancar el worker."
[ -f "$ENV_FILE" ] && set -a && . "$ENV_FILE" && set +a
[ -f "$WORKER_SCRIPT" ] || echo "[install][WARN] no existe $WORKER_SCRIPT (worker)."
[ -f "$LOG_SERVER_SCRIPT" ] || echo "[install][WARN] no existe $LOG_SERVER_SCRIPT (log server)."
[ -x "/usr/bin/env" ] || echo "[install][WARN] no existe /usr/bin/env."
[ -x "$PA_HOME/.local/bin/claude" ] || command -v claude >/dev/null 2>&1 || echo "[install][WARN] no se encontró Claude CLI en PATH ni en $PA_HOME/.local/bin/claude."
[ -f "$PA_PROJECT_DIR/.mcp.json" ] || echo "[install][WARN] no existe $PA_PROJECT_DIR/.mcp.json."
[ -f "$PA_PROJECT_DIR/tools/run_leantime_mcp.sh" ] || echo "[install][WARN] no existe $PA_PROJECT_DIR/tools/run_leantime_mcp.sh (solo necesario para setups legacy con bridge stdio)."
[ -n "${LEANTIME_MCP_URL:-}" ] || echo "[install][WARN] LEANTIME_MCP_URL no está definido en $ENV_FILE."
[ -n "${LEANTIME_MCP_TOKEN:-}" ] || echo "[install][WARN] LEANTIME_MCP_TOKEN no está definido en $ENV_FILE. Para Claude + MCP remoto HTTP usa preferiblemente un PAT de Leantime."
[ -x "$DEPLOY_SCRIPT" ] || echo "[install][WARN] no existe $DEPLOY_SCRIPT, ¿faltan tools/?"

# Marca el repo como safe para git incluso ejecutándose como root
git config --system --add safe.directory "$PA_PROJECT_DIR" >/dev/null 2>&1 || true

SUDOERS_FILE="/etc/sudoers.d/pixelagents-restart"
if [ "$WITH_SUDOERS" = "1" ]; then
  # OPT-IN: permite a $PA_USER reiniciar las units pixelagents-* sin password.
  # No es necesario para el flujo de auto-deploy (corre como root vía timer);
  # se añade sólo si invocas con --with-sudoers porque toca /etc/sudoers.d.
  cat > "$SUDOERS_FILE" <<EOF
# Generado por install_pixelagents.sh
$PA_USER ALL=(root) NOPASSWD: /bin/systemctl restart pixelagents-*, /bin/systemctl try-restart pixelagents-*, /bin/systemctl status pixelagents-*
EOF
  chmod 0440 "$SUDOERS_FILE"
  visudo -cf "$SUDOERS_FILE" >/dev/null || {
    echo "[install][ERROR] $SUDOERS_FILE inválido" >&2
    rm -f "$SUDOERS_FILE"
    exit 1
  }
  echo "[install] sudoers añadido en $SUDOERS_FILE"
else
  SUDOERS_FILE=""
fi

# Worker
cat > "$WORKER_UNIT" <<EOF
[Unit]
Description=PixelAgents Leantime MCP worker
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
User=$PA_USER
EnvironmentFile=$ENV_FILE
Environment=HOME=$PA_HOME
Environment=PATH=$PA_HOME/.local/bin:$PA_HOME/.npm-global/bin:/usr/local/bin:/usr/bin:/bin
WorkingDirectory=$PA_PROJECT_DIR
ExecStart=/usr/bin/env node $WORKER_SCRIPT
Restart=on-failure
RestartSec=10
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

# Servidor de logs (visor con index custom; cae a un http.server si falta el script)
if [ -f "$LOG_SERVER_SCRIPT" ]; then
  LOG_EXEC_START="/usr/bin/python3 $LOG_SERVER_SCRIPT --root $LOG_DIR --port $LOG_PORT --bind 0.0.0.0"
else
  echo "[install][WARN] no existe $LOG_SERVER_SCRIPT, usando http.server básico (sin index custom)."
  LOG_EXEC_START="/usr/bin/python3 -m http.server $LOG_PORT --bind 0.0.0.0"
fi

cat > "$LOG_UNIT" <<EOF
[Unit]
Description=PixelAgents log viewer (HTTP)
After=network-online.target

[Service]
Type=simple
User=$PA_USER
WorkingDirectory=$LOG_DIR
ExecStart=$LOG_EXEC_START
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

# Deploy oneshot (corre como root, hace runuser para git)
cat > "$DEPLOY_UNIT" <<EOF
[Unit]
Description=PixelAgents auto-deploy desde $DEPLOY_BRANCH
After=network-online.target

[Service]
Type=oneshot
Environment=PA_PROJECT_DIR=$PA_PROJECT_DIR
Environment=PA_PROJECT_USER=$PA_USER
Environment=PA_DEPLOY_BRANCH=$DEPLOY_BRANCH
Environment=PA_LOG_DIR=$LOG_DIR
ExecStart=$DEPLOY_SCRIPT
EOF

# Timer
cat > "$DEPLOY_TIMER" <<EOF
[Unit]
Description=Trigger PixelAgents auto-deploy

[Timer]
OnBootSec=2min
OnUnitActiveSec=$DEPLOY_INTERVAL
AccuracySec=10s
Unit=pixelagents-deploy.service

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload

if [ "$DO_ENABLE" = "1" ]; then
  systemctl enable --now pixelagents-leantime-worker.service || echo "[install][WARN] worker no arrancó (revisa journalctl -u pixelagents-leantime-worker)"
  systemctl enable --now pixelagents-log-server.service     || echo "[install][WARN] log-server no arrancó"
  systemctl enable --now pixelagents-deploy.timer           || echo "[install][WARN] deploy timer no arrancó"
else
  echo "[install] --no-enable: units instaladas pero no activadas."
fi

echo
echo "[install] Listo. Estado:"
systemctl --no-pager status pixelagents-leantime-worker.service pixelagents-log-server.service pixelagents-deploy.timer 2>/dev/null | head -40 || true

cat <<EOF

Comandos útiles:
  sudo systemctl status pixelagents-leantime-worker.service
  sudo systemctl status pixelagents-log-server.service
  sudo systemctl list-timers pixelagents-deploy.timer
  journalctl -u pixelagents-leantime-worker -f
  journalctl -u pixelagents-deploy -n 50

Disparo manual de deploy:
  sudo systemctl start pixelagents-deploy.service

Revertir todo:
  sudo systemctl disable --now pixelagents-leantime-worker.service pixelagents-log-server.service pixelagents-deploy.timer
  sudo rm -f $WORKER_UNIT $LOG_UNIT $DEPLOY_UNIT $DEPLOY_TIMER ${SUDOERS_FILE:-/etc/sudoers.d/pixelagents-restart}
  sudo systemctl daemon-reload
EOF
