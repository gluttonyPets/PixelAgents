#!/usr/bin/env bash
set -euo pipefail

LEANTIME_MCP_COMMAND="${LEANTIME_MCP_COMMAND:-leantime-mcp}"
LEANTIME_MCP_URL="${LEANTIME_MCP_URL:-}"
LEANTIME_MCP_TOKEN="${LEANTIME_MCP_TOKEN:-${LEANTIME_API_KEY:-}}"
LEANTIME_MCP_AUTH_METHOD="${LEANTIME_MCP_AUTH_METHOD:-}"

if [ -z "$LEANTIME_MCP_URL" ]; then
  echo "[leantime-mcp-wrapper] missing LEANTIME_MCP_URL" >&2
  exit 1
fi

if [ -z "$LEANTIME_MCP_TOKEN" ]; then
  echo "[leantime-mcp-wrapper] missing LEANTIME_MCP_TOKEN or LEANTIME_API_KEY" >&2
  exit 1
fi

exec node "$LEANTIME_MCP_DIST" \
  "$LEANTIME_MCP_URL" \
  --token "$LEANTIME_MCP_TOKEN" \
  --auth-method "$LEANTIME_MCP_AUTH_METHOD"
