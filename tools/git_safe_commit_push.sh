#!/usr/bin/env bash
set -euo pipefail

TICKET_ID="${1:-}"
COMMIT_MESSAGE="${2:-}"

if [ -z "$TICKET_ID" ]; then
  echo "ERROR: falta TICKET_ID"
  echo "Uso: $0 TICKET_ID \"mensaje de commit\""
  exit 1
fi

if [ -z "$COMMIT_MESSAGE" ]; then
  COMMIT_MESSAGE="Leantime #${TICKET_ID}: cambios automáticos"
fi

if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "ERROR: no estás dentro de un repo git"
  exit 1
fi

BRANCH="$(git branch --show-current)"

if [ -z "$BRANCH" ]; then
  echo "ERROR: no se pudo detectar la rama actual"
  exit 1
fi

case "$BRANCH" in
  master|main|develop)
    echo "ERROR: no se permite commit/push automático desde rama protegida: $BRANCH"
    exit 1
    ;;
esac

case "$BRANCH" in
  feature/*|fix/*|hotfix/*)
    ;;
  *)
    echo "ERROR: la rama debe empezar por feature/, fix/ o hotfix/. Rama actual: $BRANCH"
    exit 1
    ;;
esac

echo "Rama segura detectada: $BRANCH"

if ! git remote get-url origin >/dev/null 2>&1; then
  echo "ERROR: no existe remote origin"
  exit 1
fi

echo "Estado antes de commit:"
git status --short

if git diff --quiet && git diff --cached --quiet && [ -z "$(git ls-files --others --exclude-standard)" ]; then
  echo "No hay cambios para commitear."
else
  git add -A
  git commit -m "$COMMIT_MESSAGE"
fi

echo "Subiendo rama al remoto:"
git push -u origin HEAD

echo "OK: rama subida correctamente."
echo "Rama: $BRANCH"
