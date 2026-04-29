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

args=("$LEANTIME_MCP_URL" "--token" "$LEANTIME_MCP_TOKEN")

if [ -n "$LEANTIME_MCP_AUTH_METHOD" ]; then
  args+=("--auth-method" "$LEANTIME_MCP_AUTH_METHOD")
fi

exec "$LEANTIME_MCP_COMMAND" "${args[@]}"
