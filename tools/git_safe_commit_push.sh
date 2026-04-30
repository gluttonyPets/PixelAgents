#!/usr/bin/env bash
# Único método autorizado para commitear y subir ramas de trabajo.
# Reglas:
#   - Solo ramas feature/*, fix/*, hotfix/*.
#   - Nunca master, main, develop.
#   - Nunca --force, ni rebase, ni merge.
#   - Bloquea commits que incluyan archivos potencialmente sensibles.
#
# Uso:
#   ./tools/git_safe_commit_push.sh TICKET_ID "mensaje opcional"
#
# Variables de entorno:
#   DRY_RUN=1   No ejecuta commit ni push, solo valida e informa.

set -euo pipefail

TICKET_ID="${1:-}"
COMMIT_MESSAGE="${2:-}"
DRY_RUN="${DRY_RUN:-0}"

log()  { printf '[git-safe] %s\n' "$*"; }
fail() { printf '[git-safe][ERROR] %s\n' "$*" >&2; exit 1; }

# --- Validación de argumentos ---------------------------------------------
[ -n "$TICKET_ID" ] || fail "falta TICKET_ID. Uso: $0 TICKET_ID \"mensaje\""

if ! [[ "$TICKET_ID" =~ ^[0-9]+$ ]]; then
  fail "TICKET_ID debe ser numérico (recibido: '$TICKET_ID')"
fi

if [ -z "$COMMIT_MESSAGE" ]; then
  COMMIT_MESSAGE="Leantime #${TICKET_ID}: cambios automáticos"
fi

# Asegura que el mensaje referencia el ticket
if ! grep -q "#${TICKET_ID}" <<<"$COMMIT_MESSAGE"; then
  COMMIT_MESSAGE="Leantime #${TICKET_ID}: ${COMMIT_MESSAGE}"
fi

# --- Bloqueo de archivos sensibles ----------------------------------------
# Evita subir secretos por descuido.
SENSITIVE_REGEX='(^|/)(\.env(\..+)?|.*\.pem|.*\.key|.*\.p12|.*\.pfx|id_rsa|id_ed25519|credentials\.json|secrets?\.ya?ml|.*\.kdbx)$'

CHANGED_FILES="$(
  {
    git diff --name-only
    git diff --cached --name-only
    git ls-files --others --exclude-standard
  } | sort -u
)"

if [ -n "$CHANGED_FILES" ]; then
  if SENSITIVE_HITS="$(printf '%s\n' "$CHANGED_FILES" | grep -E "$SENSITIVE_REGEX" || true)"; [ -n "$SENSITIVE_HITS" ]; then
    log "Archivos potencialmente sensibles detectados:"
    printf '  - %s\n' $SENSITIVE_HITS >&2
    fail "abortado por seguridad. Quita esos archivos del staging o añádelos a .gitignore."
  fi
fi

# --- Estado actual ---------------------------------------------------------
log "Estado antes de commit:"
git status --short || true

NOTHING_TO_DO=0
if git diff --quiet \
   && git diff --cached --quiet \
   && [ -z "$(git ls-files --others --exclude-standard)" ]; then
  NOTHING_TO_DO=1
fi

# --- Dry-run ---------------------------------------------------------------
if [ "$DRY_RUN" = "1" ]; then
  log "DRY_RUN=1 → no se ejecuta commit ni push."
  log "Mensaje de commit que se usaría: $COMMIT_MESSAGE"
  if [ "$NOTHING_TO_DO" = "1" ]; then
    log "(sin cambios para commitear)"
  fi
  exit 0
fi

# --- Commit ----------------------------------------------------------------
if [ "$NOTHING_TO_DO" = "1" ]; then
  log "No hay cambios locales nuevos. Se intentará igualmente publicar la rama."
else
  git add -A
  git commit -m "$COMMIT_MESSAGE"
  log "Commit creado: $(git log -1 --pretty='%h %s')"
fi

# --- Push ------------------------------------------------------------------
log "Subiendo rama al remoto (sin --force):"
git push -u origin "HEAD:${BRANCH}"

REMOTE_URL="$(git remote get-url origin)"
log "OK: rama publicada."
log "Rama:   $BRANCH"
log "Remoto: $REMOTE_URL"
