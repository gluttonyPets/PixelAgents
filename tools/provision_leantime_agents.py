#!/usr/bin/env python3
"""
Provisiona en Leantime los 5 usuarios que representan a los agentes.
Cada agente puede así ser asignado como editor de subtareas.

- Idempotente: si un usuario ya existe (mismo email), se reutiliza.
- Guarda el mapping nombre→userId en
  ~/.config/pixelagents/agent_users.json
  (configurable con PIXELAGENTS_AGENT_USERS_FILE).
- No imprime las contraseñas; los agentes no inician sesión, sólo se
  usan como destinatarios de asignación.

Uso:
  tools/provision_leantime_agents.py [--role 30] [--domain pixelagents.local]

Variables de entorno (en LEANTIME_API_URL/KEY o en
~/.config/pixelagents/leantime.env):
  LEANTIME_API_URL
  LEANTIME_API_KEY
"""
from __future__ import annotations

import argparse
import json
import os
import secrets
import string
import sys
import urllib.request
from pathlib import Path

AGENTS = [
    "coordinator",
    "backend-implementer",
    "frontend-implementer",
    "test-runner",
    "code-reviewer",
]

DEFAULT_DOMAIN = "pixelagents.local"
# Roles típicos de Leantime: 5=admin, 20=manager, 30=editor,
# 40=commenter, 50=clientManager. 30 (editor) permite asignación.
DEFAULT_ROLE = 30


def load_env_file(path: Path) -> None:
    if not path.exists():
        return
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, val = line.split("=", 1)
        val = val.strip().strip("\"'")
        os.environ.setdefault(key.strip(), val)


def rpc(url: str, api_key: str, method: str, params: dict) -> dict:
    payload = json.dumps({
        "jsonrpc": "2.0",
        "id": 1,
        "method": method,
        "params": params,
    }).encode("utf-8")
    req = urllib.request.Request(url, data=payload, headers={
        "Content-Type": "application/json",
        "x-api-key": api_key,
    }, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read().decode("utf-8"))
    except Exception as e:
        return {"error": {"message": f"RPC transport failure: {e}"}}


def random_password(n: int = 32) -> str:
    alphabet = string.ascii_letters + string.digits
    return "".join(secrets.choice(alphabet) for _ in range(n))


def find_existing_user(users: list[dict], email: str) -> dict | None:
    e = email.lower()
    for u in users:
        for key in ("user", "username", "email"):
            v = u.get(key) if isinstance(u, dict) else None
            if v and str(v).lower() == e:
                return u
    return None


def main() -> int:
    home = Path(os.environ.get("HOME", "."))
    config_dir = Path(os.environ.get(
        "PIXELAGENTS_CONFIG_DIR", home / ".config/pixelagents"
    ))
    env_file = Path(os.environ.get(
        "PIXELAGENTS_ENV_FILE", config_dir / "leantime.env"
    ))
    agents_file = Path(os.environ.get(
        "PIXELAGENTS_AGENT_USERS_FILE", config_dir / "agent_users.json"
    ))

    load_env_file(env_file)

    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--role", type=int, default=DEFAULT_ROLE,
                   help=f"Rol Leantime (default {DEFAULT_ROLE} = editor)")
    p.add_argument("--domain", default=DEFAULT_DOMAIN,
                   help=f"Dominio de email (default {DEFAULT_DOMAIN})")
    args = p.parse_args()

    url = os.environ.get("LEANTIME_API_URL")
    api_key = os.environ.get("LEANTIME_API_KEY")
    if not (url and api_key):
        print("ERROR: faltan LEANTIME_API_URL / LEANTIME_API_KEY", file=sys.stderr)
        return 1

    # Lista usuarios actuales para evitar duplicados
    list_resp = rpc(url, api_key, "leantime.rpc.users.getAll", {})
    if "error" in list_resp:
        print(f"ERROR listando usuarios: {list_resp['error']}", file=sys.stderr)
        return 2
    users = list_resp.get("result") or []
    if not isinstance(users, list):
        users = []

    mapping: dict[str, int] = {}
    if agents_file.exists():
        try:
            mapping = json.loads(agents_file.read_text())
            if not isinstance(mapping, dict):
                mapping = {}
        except Exception:
            mapping = {}

    for agent in AGENTS:
        email = f"{agent}@{args.domain}"
        existing = find_existing_user(users, email)

        if existing:
            uid_raw = existing.get("id")
            try:
                uid = int(uid_raw)
                mapping[agent] = uid
                print(f"[OK ] {agent:<22} ya existía como id={uid}")
                continue
            except (TypeError, ValueError):
                print(f"[WARN] {agent}: usuario existe pero id no parseable: {uid_raw!r}",
                      file=sys.stderr)
                continue

        first, _, rest = agent.partition("-")
        firstname = first.capitalize() or agent
        lastname = rest.capitalize() if rest else "Agent"

        create_params = {
            "values": {
                "user": email,
                "firstname": firstname,
                "lastname": lastname,
                "password": random_password(),
                "role": args.role,
                "phone": "",
                "source": "pixelagents",
                "status": "a",
                "jobTitle": f"PixelAgents {firstname} {lastname}".strip(),
            }
        }
        res = rpc(url, api_key, "leantime.rpc.users.addUser", create_params)
        if "error" in res:
            print(f"[ERR ] {agent}: no se pudo crear: {res['error']}", file=sys.stderr)
            continue

        result = res.get("result")
        uid = None
        if isinstance(result, dict):
            uid = result.get("id") or result.get("userId")
        elif isinstance(result, (int, str)):
            uid = result

        try:
            uid_int = int(uid)
        except (TypeError, ValueError):
            print(f"[ERR ] {agent}: respuesta inesperada de addUser: {res}", file=sys.stderr)
            continue

        mapping[agent] = uid_int
        print(f"[NEW] {agent:<22} creado id={uid_int} (email={email})")

    agents_file.parent.mkdir(parents=True, exist_ok=True)
    agents_file.write_text(json.dumps(mapping, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")

    print()
    print(f"Mapping guardado en {agents_file}")
    print(json.dumps(mapping, indent=2, ensure_ascii=False))
    return 0 if len(mapping) == len(AGENTS) else 3


if __name__ == "__main__":
    sys.exit(main())
