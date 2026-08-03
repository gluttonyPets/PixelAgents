#!/usr/bin/env bash
# Inventario de solo lectura del despliegue de PixelAgents en una máquina.
#
# Responde a: "¿qué hay instalado aquí que despliegue esto, y por qué no corre?"
#
# NO modifica nada: solo lee estado de systemd, cron, docker y git.
#
# Uso:
#   ./tools/diagnose_deploy.sh              # desde la raíz del repo
#   sudo ./tools/diagnose_deploy.sh         # más detalle (journal completo)
#   PA_PROJECT_DIR=/srv/PixelAgents ./tools/diagnose_deploy.sh
#
# Variables:
#   PA_PROJECT_DIR   Ruta al checkout (default: raíz del repo que contiene este script)

set -uo pipefail

PROJECT_DIR="${PA_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"

UNITS=(
  pixelagents-deploy.timer
  pixelagents-deploy.service
  pixelagents-leantime-worker.service
  pixelagents-log-server.service
)

section() { printf '\n\033[1m━━ %s ━━\033[0m\n' "$*"; }
note()    { printf '  %s\n' "$*"; }
warn()    { printf '  \033[33m! %s\033[0m\n' "$*"; }
bad()     { printf '  \033[31m✗ %s\033[0m\n' "$*"; }
ok()      { printf '  \033[32m✓ %s\033[0m\n' "$*"; }

have() { command -v "$1" >/dev/null 2>&1; }

printf '\033[1mPixelAgents · diagnóstico de despliegue\033[0m\n'
note "Fecha:   $(date -Iseconds)"
note "Host:    $(hostname 2>/dev/null || echo '?')"
note "Repo:    $PROJECT_DIR"
note "Usuario: $(id -un)"

# ─────────────────────────────────────────────────────────────
section "1. Unidades systemd de PixelAgents"

if ! have systemctl; then
  warn "systemctl no disponible: esta máquina no usa systemd."
else
  for unit in "${UNITS[@]}"; do
    if systemctl list-unit-files "$unit" >/dev/null 2>&1 \
       && systemctl cat "$unit" >/dev/null 2>&1; then
      enabled="$(systemctl is-enabled "$unit" 2>/dev/null || echo 'unknown')"
      active="$(systemctl is-active "$unit" 2>/dev/null || echo 'unknown')"
      case "$active" in
        active|activating) ok  "$unit  [enabled=$enabled active=$active]" ;;
        inactive)          note "$unit  [enabled=$enabled active=$active]" ;;
        failed)            bad "$unit  [enabled=$enabled active=$active]" ;;
        *)                 warn "$unit  [enabled=$enabled active=$active]" ;;
      esac
    else
      bad "$unit  NO INSTALADA"
    fi
  done

  section "2. Próximo disparo del timer de auto-deploy"
  systemctl list-timers pixelagents-deploy.timer --all --no-pager 2>/dev/null \
    || warn "no se pudo listar el timer"

  section "3. Rama configurada para auto-deploy"
  if systemctl cat pixelagents-deploy.service >/dev/null 2>&1; then
    systemctl cat pixelagents-deploy.service 2>/dev/null \
      | grep -E 'Environment=|ExecStart=' | sed 's/^/  /'
  else
    bad "pixelagents-deploy.service no instalada: NO hay auto-deploy por systemd."
  fi

  section "4. Últimos resultados del auto-deploy (journal)"
  if journalctl -u pixelagents-deploy.service -n 30 --no-pager >/dev/null 2>&1; then
    journalctl -u pixelagents-deploy.service -n 30 --no-pager 2>/dev/null | sed 's/^/  /'
  else
    warn "sin acceso al journal (prueba con sudo)."
  fi
fi

# ─────────────────────────────────────────────────────────────
section "5. Log propio del script de deploy"
DEPLOY_LOG="$PROJECT_DIR/automation/logs/deploy.log"
if [ -f "$DEPLOY_LOG" ]; then
  note "$DEPLOY_LOG (últimas 25 líneas)"
  tail -25 "$DEPLOY_LOG" | sed 's/^/  /'
else
  warn "no existe $DEPLOY_LOG (el auto-deploy quizá nunca ha corrido aquí)."
fi

# ─────────────────────────────────────────────────────────────
section "6. Cron / otros automatismos"
found_cron=0
for f in /etc/crontab /etc/cron.d/*; do
  [ -f "$f" ] || continue
  if grep -qiE 'pixelagents|deploy' "$f" 2>/dev/null; then
    note "$f:"; grep -iE 'pixelagents|deploy' "$f" | sed 's/^/    /'; found_cron=1
  fi
done
if crontab -l >/dev/null 2>&1; then
  if crontab -l 2>/dev/null | grep -qiE 'pixelagents|deploy'; then
    note "crontab de $(id -un):"
    crontab -l 2>/dev/null | grep -iE 'pixelagents|deploy' | sed 's/^/    /'; found_cron=1
  fi
fi
[ "$found_cron" -eq 0 ] && note "sin entradas de cron relacionadas."

# ─────────────────────────────────────────────────────────────
section "7. Estado del checkout git"
if [ ! -d "$PROJECT_DIR/.git" ]; then
  bad "$PROJECT_DIR no es un repo git."
else
  git config --global --add safe.directory "$PROJECT_DIR" >/dev/null 2>&1 || true
  CUR="$(git -C "$PROJECT_DIR" branch --show-current 2>/dev/null || echo '?')"
  note "Rama actual:  $CUR"
  note "HEAD:         $(git -C "$PROJECT_DIR" log -1 --format='%h %ad %s' --date=short 2>/dev/null || echo '?')"
  note "Propietario:  $(stat -c %U "$PROJECT_DIR/.git" 2>/dev/null || echo '?')"

  if git -C "$PROJECT_DIR" diff --quiet 2>/dev/null && git -C "$PROJECT_DIR" diff --cached --quiet 2>/dev/null; then
    ok "working tree limpio"
  else
    bad "working tree CON CAMBIOS LOCALES → deploy_develop.sh aborta con exit 4"
    git -C "$PROJECT_DIR" status --short 2>/dev/null | head -20 | sed 's/^/    /'
  fi

  if git -C "$PROJECT_DIR" fetch --quiet origin "$CUR" 2>/dev/null; then
    LOCAL="$(git -C "$PROJECT_DIR" rev-parse "$CUR" 2>/dev/null || echo '')"
    REMOTE="$(git -C "$PROJECT_DIR" rev-parse "origin/$CUR" 2>/dev/null || echo '')"
    if [ -n "$LOCAL" ] && [ -n "$REMOTE" ]; then
      if [ "$LOCAL" = "$REMOTE" ]; then
        ok "al día con origin/$CUR"
      elif git -C "$PROJECT_DIR" merge-base --is-ancestor "$LOCAL" "$REMOTE" 2>/dev/null; then
        warn "hay $(git -C "$PROJECT_DIR" rev-list --count "$LOCAL..$REMOTE") commit(s) pendientes en origin/$CUR (fast-forward posible)"
      else
        bad "la rama local DIVERGIÓ de origin/$CUR → deploy_develop.sh aborta con exit 2"
      fi
    fi
  else
    warn "no se pudo hacer fetch de origin/$CUR"
  fi
fi

# ─────────────────────────────────────────────────────────────
section "8. Docker (aquí es donde se compila la app .NET)"
if ! have docker; then
  bad "docker no instalado: la app .NET no puede construirse en esta máquina."
elif ! docker info >/dev/null 2>&1; then
  bad "docker instalado pero el daemon NO responde (¿parado? ¿faltan permisos?)"
  note "prueba: sudo systemctl status docker"
else
  ok "daemon docker operativo"
  note "Contenedores del proyecto:"
  docker compose -f "$PROJECT_DIR/docker-compose.yml" ps 2>/dev/null | sed 's/^/    /' \
    || docker ps --filter name=pixelagents --format '    {{.Names}}\t{{.Status}}' 2>/dev/null \
    || warn "no se pudieron listar contenedores"
  note "Imagen y antigüedad del build:"
  docker images --filter reference='*pixelagents*' \
    --format '    {{.Repository}}:{{.Tag}}  creada {{.CreatedSince}}' 2>/dev/null \
    || true
fi

# ─────────────────────────────────────────────────────────────
section "9. Marcadores de estado"
BUSY_FILE="${PA_BUSY_FILE:-/tmp/pixelagents-worker-busy}"
if [ -f "$BUSY_FILE" ]; then
  warn "existe $BUSY_FILE → el auto-deploy NO reinicia el worker (creado $(stat -c %y "$BUSY_FILE" 2>/dev/null))"
  warn "si es antiguo, es un fichero huérfano y bloquea reinicios del worker."
else
  ok "sin busy file"
fi
[ -f /tmp/pixelagents-deploy.lock ] && note "lock de deploy presente (normal si hay uno en curso)"

# ─────────────────────────────────────────────────────────────
section "Resumen"
cat <<'EOF'
  Recuerda cómo está montado esto:

    · pixelagents-deploy.timer → deploy_develop.sh
        Solo hace git fast-forward + reinicia worker y log-server.
        NO compila. NO reconstruye la imagen Docker.

    · deploy.sh (manual)
        git pull origin main + docker compose build --no-cache + up -d.
        ESTA es la que compila y despliega la app en producción.

  Si el código nuevo no aparece en producción, casi siempre es una de estas:
    1. deploy.sh no se ha ejecutado (nadie reconstruyó la imagen).
    2. deploy_develop.sh aborta: exit 2 (divergencia), 3 (rama), 4 (tree sucio).
    3. La rama del timer (PA_DEPLOY_BRANCH) no es la rama a la que subes.
EOF
