#!/usr/bin/env bash
# Auto-deploy de la rama develop:
#   - hace fetch
#   - verifica que el repo está limpio y sin divergencia
#   - fast-forward (nunca merge ni rebase)
#   - reinicia los servicios systemd de PixelAgents
#
# Pensado para ejecutarse:
#   - como root vía pixelagents-deploy.timer (1 min por defecto)
#   - manualmente con `sudo ./tools/deploy_develop.sh`
#
# Variables de entorno:
#   PA_PROJECT_DIR    Ruta al checkout (default: directorio actual)
#   PA_PROJECT_USER   Usuario propietario del repo (default: dueño del .git)
#   PA_DEPLOY_BRANCH  Rama a desplegar (default: develop)
#   PA_LOG_DIR        Donde escribir deploy.log (default: $PA_PROJECT_DIR/automation/logs)
#   PA_RESTART_UNITS  Lista separada por espacios de units a reiniciar
#                     (default: pixelagents-leantime-worker.service pixelagents-log-server.service)
#   PA_BUSY_FILE      Si existe, se omite el reinicio del worker
#                     (default: /tmp/pixelagents-worker-busy)
#
# Códigos de salida:
#   0  ok (sin cambios o desplegado)
#   1  error de configuración / repo
#   2  develop divergió, no se hace nada
#   3  rama actual no es la rama de deploy
#   4  working tree con cambios locales

set -euo pipefail

PROJECT_DIR="${PA_PROJECT_DIR:-$PWD}"
BRANCH="${PA_DEPLOY_BRANCH:-develop}"
LOG_DIR="${PA_LOG_DIR:-$PROJECT_DIR/automation/logs}"
LOG_FILE="$LOG_DIR/deploy.log"
LOCK_FILE="/tmp/pixelagents-deploy.lock"
BUSY_FILE="${PA_BUSY_FILE:-/tmp/pixelagents-worker-busy}"
RESTART_UNITS="${PA_RESTART_UNITS:-pixelagents-leantime-worker.service pixelagents-log-server.service}"

mkdir -p "$LOG_DIR"
ts() { date -Iseconds; }
log() { printf '[%s] %s\n' "$(ts)" "$*" | tee -a "$LOG_FILE"; }
fail() { printf '[%s][ERROR] %s\n' "$(ts)" "$*" | tee -a "$LOG_FILE" >&2; exit "${2:-1}"; }

# Lock para evitar deploys concurrentes
exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  log "Deploy ya en curso, salgo."
  exit 0
fi

[ -d "$PROJECT_DIR/.git" ] || fail "no es un repo git: $PROJECT_DIR"

# Determinar usuario propietario del repo si no viene por env
if [ -z "${PA_PROJECT_USER:-}" ]; then
  PA_PROJECT_USER="$(stat -c %U "$PROJECT_DIR/.git" 2>/dev/null || echo "")"
fi
[ -n "$PA_PROJECT_USER" ] || fail "no he podido determinar PA_PROJECT_USER"

# Helper: ejecuta git como el usuario del repo (evita líos de ownership)
run_git() {
  if [ "$(id -un)" = "$PA_PROJECT_USER" ]; then
    git -C "$PROJECT_DIR" "$@"
  else
    runuser -u "$PA_PROJECT_USER" -- git -C "$PROJECT_DIR" "$@"
  fi
}

# Marcar el dir como safe para git (Debian es estricto con dubious ownership)
git config --global --add safe.directory "$PROJECT_DIR" >/dev/null 2>&1 || true

run_git fetch --quiet origin "$BRANCH"

LOCAL_SHA="$(run_git rev-parse "$BRANCH" 2>/dev/null || echo "")"
REMOTE_SHA="$(run_git rev-parse "origin/$BRANCH")"

if [ -z "$LOCAL_SHA" ]; then
  fail "no existe rama local $BRANCH; haz checkout primero" 1
fi

if [ "$LOCAL_SHA" = "$REMOTE_SHA" ]; then
  # Sin cambios: silencioso, no contamina el log
  exit 0
fi

# Comprobamos que local es ancestro de remoto (fast-forward posible)
if ! run_git merge-base --is-ancestor "$LOCAL_SHA" "$REMOTE_SHA"; then
  fail "rama $BRANCH local divergió de origin/$BRANCH; no se hace deploy" 2
fi

CURRENT_BRANCH="$(run_git branch --show-current)"
if [ "$CURRENT_BRANCH" != "$BRANCH" ]; then
  fail "el repo no está en la rama $BRANCH (actual: $CURRENT_BRANCH); no se hace deploy" 3
fi

if ! run_git diff --quiet || ! run_git diff --cached --quiet; then
  fail "working tree con cambios locales en $BRANCH; no se hace deploy" 4
fi

log "Cambios detectados en $BRANCH: $LOCAL_SHA -> $REMOTE_SHA"
run_git merge --ff-only "origin/$BRANCH"
log "Fast-forward aplicado."

# Reinstalar dependencias si requirements.txt cambió
if run_git diff --name-only "$LOCAL_SHA" "$REMOTE_SHA" | grep -q '^requirements.txt$'; then
  if [ -x "$PROJECT_DIR/.venv/bin/pip" ]; then
    log "requirements.txt cambió, actualizando venv..."
    runuser -u "$PA_PROJECT_USER" -- "$PROJECT_DIR/.venv/bin/pip" install --quiet -r "$PROJECT_DIR/requirements.txt" \
      || log "AVISO: pip install falló, revisar manualmente."
  fi
fi

# Reinicio de servicios. Si hay busy file, sólo reiniciamos los no-worker.
RESTART_LIST=()
for unit in $RESTART_UNITS; do
  if [ -f "$BUSY_FILE" ] && [[ "$unit" == *worker* ]]; then
    log "Worker ocupado ($BUSY_FILE existe), pospongo reinicio de $unit"
    continue
  fi
  RESTART_LIST+=("$unit")
done

if [ ${#RESTART_LIST[@]} -gt 0 ]; then
  if command -v systemctl >/dev/null 2>&1; then
    log "Reiniciando: ${RESTART_LIST[*]}"
    systemctl try-restart "${RESTART_LIST[@]}" \
      || log "AVISO: try-restart devolvió error (¿units no instaladas?)"
  else
    log "AVISO: systemctl no disponible, no reinicio servicios."
  fi
fi

log "Deploy OK ($REMOTE_SHA)"
