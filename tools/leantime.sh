#!/usr/bin/env bash
set -euo pipefail

source "/home/debian/.config/pixelagents/leantime.env"

call() {
  local payload="$1"
  curl -sS \
    -H "x-api-key: ${LEANTIME_API_KEY}" \
    -H "Content-Type: application/json" \
    -X POST "${LEANTIME_API_URL}" \
    --data "${payload}"
}

usage() {
  echo "Uso:"
  echo "  $0 list"
  echo "  $0 raw '<json>'"
  echo "  $0 create \"titulo\" \"descripcion\""
  echo "  $0 show ID"
  exit 1
}

case "${1:-}" in
  list)
    call '{"jsonrpc":"2.0","id":1,"method":"leantime.rpc.tickets.getAll","params":{}}'
    ;;
  raw)
    [ $# -ge 2 ] || usage
    call "$2"
    ;;
  create)
    [ $# -ge 3 ] || usage
    TITLE="$2"
    DESC="$3"
    call "{
      \"jsonrpc\":\"2.0\",
      \"id\":1,
      \"method\":\"leantime.rpc.tickets.addTicket\",
      \"params\":{
        \"values\":{
          \"headline\":\"${TITLE}\",
          \"description\":\"${DESC}\",
          \"projectId\":${LEANTIME_PROJECT_ID},
          \"type\":\"task\"
        }
      }
    }"
    ;;
  show)
    [ $# -ge 2 ] || usage
    ID="$2"
    call "{
      \"jsonrpc\":\"2.0\",
      \"id\":1,
      \"method\":\"leantime.rpc.tickets.getTicket\",
      \"params\":{\"id\":${ID}}
    }"
    ;;
  *)
    usage
    ;;
esac
