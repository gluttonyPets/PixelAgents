#!/usr/bin/env python3
"""
Crea una subtarea Leantime asociada a un ticket padre y, si se indica,
la asigna a un agente (mapping en agent_users.json).

Uso:
  tools/leantime_subtask.py PARENT_ID HEADLINE
                            [--description TEXT]
                            [--agent NAME]
                            [--type subtask|task]
                            [--quiet]

Variables:
  LEANTIME_API_URL, LEANTIME_API_KEY, LEANTIME_PROJECT_ID
  PIXELAGENTS_AGENT_USERS_FILE (default: ~/.config/pixelagents/agent_users.json)
  PIXELAGENTS_ENV_FILE         (default: ~/.config/pixelagents/leantime.env)

Salida:
  Por defecto imprime la respuesta JSON del RPC. Con --quiet imprime sólo
  el id del nuevo ticket o nada si falla. Exit 0 si OK, 2 si error RPC.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.request
from pathlib import Path


def load_env_file(path: Path) -> None:
    if not path.exists():
        return
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, val = line.split("=", 1)
        os.environ.setdefault(key.strip(), val.strip().strip("\"'"))


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
    p.add_argument("parent_id", type=int, help="ID del ticket padre")
    p.add_argument("headline", help="Título de la subtarea")
    p.add_argument("--description", "-d", default="", help="Descripción libre")
    p.add_argument("--agent", "-a", default=None,
                   help="Agente al que asignar (ver agent_users.json)")
    p.add_argument("--type", default="subtask",
                   choices=["subtask", "task"], help="Tipo de ticket")
    p.add_argument("--quiet", "-q", action="store_true",
                   help="Imprime sólo el id del ticket creado")
    args = p.parse_args()

    url = os.environ.get("LEANTIME_API_URL")
    api_key = os.environ.get("LEANTIME_API_KEY")
    project_id = os.environ.get("LEANTIME_PROJECT_ID")
    if not (url and api_key and project_id):
        print("ERROR: faltan LEANTIME_API_URL / LEANTIME_API_KEY / LEANTIME_PROJECT_ID",
              file=sys.stderr)
        return 1

    values = {
        "projectId": int(project_id),
        "headline": args.headline,
        "description": args.description,
        "type": args.type,
        "dependingTicketId": int(args.parent_id),
    }

    if args.agent:
        if not agents_file.exists():
            print(f"[WARN] {agents_file} no existe; "
                  f"ejecuta provision_leantime_agents.py primero. "
                  f"Subtarea sin asignación.", file=sys.stderr)
        else:
            try:
                mapping = json.loads(agents_file.read_text())
            except Exception as e:
                print(f"[WARN] no pude leer {agents_file}: {e}", file=sys.stderr)
                mapping = {}
            uid = mapping.get(args.agent)
            if uid is None:
                print(f"[WARN] agente '{args.agent}' no está en {agents_file}",
                      file=sys.stderr)
            else:
                try:
                    values["editorId"] = int(uid)
                except (TypeError, ValueError):
                    print(f"[WARN] userId no parseable para {args.agent}: {uid!r}",
                          file=sys.stderr)

    res = rpc(url, api_key, "leantime.rpc.tickets.addTicket", {"values": values})

    if "error" in res:
        print(json.dumps(res, ensure_ascii=False, indent=2), file=sys.stderr)
        return 2

    result = res.get("result")
    new_id = None
    if isinstance(result, dict):
        new_id = result.get("id") or result.get("ticketId")
    elif isinstance(result, (int, str)):
        new_id = result

    if args.quiet:
        if new_id is not None:
            print(new_id)
    else:
        print(json.dumps(res, ensure_ascii=False, indent=2))

    return 0


if __name__ == "__main__":
    sys.exit(main())
