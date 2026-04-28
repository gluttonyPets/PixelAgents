#!/usr/bin/env python3
"""
Servidor HTTP del visor de logs de PixelAgents.

- En `/` muestra un index agrupado por ticket (carpeta o archivos sueltos).
- Para el resto de rutas se comporta como un servidor estático normal,
  por lo que los reports HTML, los live.log y los events.json se sirven igual.

Layouts soportados:
  Nuevo (preferido):  ticket-<id>/live.log, events.json, report.html, ...
  Antiguo (flat):     ticket-<id>.live.log, ticket-<id>.events.json, ...

Variables / argumentos:
  --root  | $PA_LOG_DIR  Directorio raíz a servir (default: cwd)
  --port  | $PA_LOG_PORT Puerto (default: 9000)
  --bind  | $PA_LOG_BIND IP a la que bindear (default: 0.0.0.0)
"""
from __future__ import annotations

import argparse
import http.server
import os
import re
import socketserver
import sys
from datetime import datetime
from pathlib import Path
from urllib.parse import quote

TICKET_DIR_RE = re.compile(r"^ticket-(\d+)$")
TICKET_FILE_RE = re.compile(r"^ticket-(\d+)\.(.+)$")

KIND_LABELS = {
    "report.html": ("Reporte",   "report"),
    "live.log":    ("Live log",  "live"),
    "events.json": ("Eventos",   "events"),
    "report.md":   ("Markdown",  "md"),
    "raw.jsonl":   ("Raw",       "raw"),
}
KIND_ORDER = ["report.html", "live.log", "events.json", "report.md", "raw.jsonl"]


def kind_of(name: str) -> str | None:
    for kind in KIND_LABELS:
        if name == kind or name.endswith("." + kind):
            return kind
    return None


def human_size(n: int) -> str:
    f = float(n)
    for unit in ("B", "KB", "MB", "GB"):
        if f < 1024:
            return f"{f:.0f} {unit}" if unit == "B" else f"{f:.1f} {unit}"
        f /= 1024
    return f"{f:.1f} TB"


def relative_time(ts: float) -> str:
    if not ts:
        return "—"
    delta = datetime.now().timestamp() - ts
    if delta < 0:
        delta = 0
    if delta < 60:
        return f"hace {int(delta)}s"
    if delta < 3600:
        return f"hace {int(delta/60)} min"
    if delta < 86400:
        return f"hace {int(delta/3600)} h"
    return datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M")


def discover_tickets(root: Path) -> list[dict]:
    tickets: dict[str, dict] = {}

    if not root.exists():
        return []

    # Layout nested: carpetas ticket-<id>/
    for entry in root.iterdir():
        if not entry.is_dir():
            continue
        m = TICKET_DIR_RE.match(entry.name)
        if not m:
            continue
        tid = m.group(1)
        t = tickets.setdefault(tid, {"id": tid, "files": []})
        for f in entry.iterdir():
            if not f.is_file():
                continue
            kind = kind_of(f.name)
            t["files"].append({
                "kind": kind or f.name,
                "name": f.name,
                "url": f"/{quote(entry.name)}/{quote(f.name)}",
                "size": f.stat().st_size,
                "mtime": f.stat().st_mtime,
            })

    # Layout flat: ticket-<id>.<kind>
    for entry in root.iterdir():
        if not entry.is_file():
            continue
        m = TICKET_FILE_RE.match(entry.name)
        if not m:
            continue
        tid, suffix = m.group(1), m.group(2)
        kind = kind_of(suffix) or suffix
        t = tickets.setdefault(tid, {"id": tid, "files": []})
        # Si ya hay versión nested del mismo kind, ignora la flat
        if any(f["kind"] == kind for f in t["files"]):
            continue
        t["files"].append({
            "kind": kind,
            "name": suffix,
            "url": f"/{quote(entry.name)}",
            "size": entry.stat().st_size,
            "mtime": entry.stat().st_mtime,
        })

    out = []
    for t in tickets.values():
        if not t["files"]:
            continue
        t["last_mtime"] = max(f["mtime"] for f in t["files"])
        # Orden estable: report primero, después live, etc.
        t["files"].sort(key=lambda f: (
            KIND_ORDER.index(f["kind"]) if f["kind"] in KIND_ORDER else 99,
            f["name"],
        ))
        out.append(t)

    out.sort(key=lambda t: (-t["last_mtime"], -int(t["id"])))
    return out


INDEX_TEMPLATE = """<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="refresh" content="10">
<title>PixelAgents · Logs</title>
<style>
:root {
  --bg: #0e1117;
  --panel: #161b22;
  --panel-2: #1c2128;
  --border: #30363d;
  --text: #e6edf3;
  --muted: #7d8590;
  --accent: #2f81f7;
  --accent-2: #1f6feb;
  --green: #3fb950;
}
* { box-sizing: border-box; }
html, body { height: 100%; }
body {
  margin: 0;
  font: 14px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
  background: var(--bg);
  color: var(--text);
}
header.top {
  position: sticky; top: 0; z-index: 5;
  background: rgba(14,17,23,.92); backdrop-filter: blur(8px);
  padding: 14px 24px;
  border-bottom: 1px solid var(--border);
  display: flex; justify-content: space-between; align-items: center; gap: 16px;
}
header.top h1 { margin: 0; font-size: 16px; font-weight: 600; letter-spacing: .2px; }
header.top .sub { color: var(--muted); font-size: 12px; }
header.top .right { display: flex; gap: 10px; align-items: center; }
header.top .right a { color: var(--accent); text-decoration: none; font-size: 13px; }
header.top .right a:hover { text-decoration: underline; }
main { padding: 20px 24px 48px; max-width: 1280px; margin: 0 auto; }
.search { width: 100%; max-width: 360px; margin-bottom: 16px;
  background: var(--panel); border: 1px solid var(--border); color: var(--text);
  padding: 8px 12px; border-radius: 6px; font: inherit; }
.search:focus { outline: none; border-color: var(--accent); }
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 12px;
}
.card {
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 14px 16px 12px;
  transition: border-color .15s, background .15s;
  position: relative;
}
.card:hover { border-color: var(--accent); background: var(--panel-2); }
.card.active::before {
  content: ""; position: absolute; left: 0; top: 8px; bottom: 8px; width: 3px;
  background: var(--green); border-radius: 0 3px 3px 0;
}
.card-head { display: flex; justify-content: space-between; align-items: baseline; }
.card-head h2 { margin: 0; font-size: 15px; font-weight: 600; }
.card-head h2 a { color: var(--text); text-decoration: none; }
.card-head h2 a:hover { color: var(--accent); }
.status { font-size: 11px; color: var(--muted); }
.card.active .status { color: var(--green); }
.meta { color: var(--muted); font-size: 12px; margin: 2px 0 10px; }
.artifacts { display: flex; flex-wrap: wrap; gap: 6px; }
.art {
  display: inline-flex; align-items: center; gap: 4px;
  padding: 4px 10px; border-radius: 5px;
  border: 1px solid var(--border); color: var(--text);
  text-decoration: none; font-size: 12px;
  background: rgba(255,255,255,.02);
}
.art:hover { background: rgba(255,255,255,.06); border-color: var(--accent); }
.art.report { background: var(--accent); border-color: var(--accent); color: #fff; }
.art.report:hover { background: var(--accent-2); }
.art.live { color: var(--green); border-color: rgba(63,185,80,.4); }
.empty { color: var(--muted); text-align: center; padding: 64px 16px; }
footer.bottom { color: var(--muted); font-size: 11px; text-align: center; padding: 16px; }
</style>
</head>
<body>
<header class="top">
  <div>
    <h1>PixelAgents · Logs</h1>
    <div class="sub">__SUB__</div>
  </div>
  <div class="right">
    <a href="/">↻ refrescar</a>
  </div>
</header>
<main>
  <input type="search" class="search" placeholder="Filtrar por ticket #..." oninput="filterCards(this.value)">
  <div class="grid" id="grid">
__CARDS__
  </div>
</main>
<footer class="bottom">Auto-refresh cada 10s · servido desde __ROOT__</footer>
<script>
function filterCards(q) {
  q = (q || "").trim().toLowerCase();
  document.querySelectorAll("#grid .card").forEach(c => {
    c.style.display = !q || c.dataset.search.includes(q) ? "" : "none";
  });
}
</script>
</body>
</html>
"""


def render_index(root: Path, tickets: list[dict]) -> str:
    if not tickets:
        cards_html = '<p class="empty">No hay tickets todavía. Crea una tarea en Leantime y muévela a Ready.</p>'
    else:
        cards = []
        now = datetime.now().timestamp()
        for t in tickets:
            is_active = (now - t["last_mtime"]) < 60
            cls = "card active" if is_active else "card"
            status = "● activo" if is_active else "idle"

            arts = []
            for f in t["files"]:
                label, css = KIND_LABELS.get(f["kind"], (f["kind"], "other"))
                arts.append(
                    f'<a class="art {css}" href="{f["url"]}" '
                    f'title="{f["name"]} · {human_size(f["size"])}">{label}</a>'
                )
            arts_html = "".join(arts)

            # Link primario: si hay report.html, abre el report; si no, el live.log; si no, el primero
            primary_url = next(
                (f["url"] for f in t["files"] if f["kind"] == "report.html"),
                next((f["url"] for f in t["files"] if f["kind"] == "live.log"),
                     t["files"][0]["url"])
            )

            cards.append(
                f'<article class="{cls}" data-search="ticket #{t["id"]} {t["id"]}">\n'
                f'  <div class="card-head">\n'
                f'    <h2><a href="{primary_url}">Ticket #{t["id"]}</a></h2>\n'
                f'    <span class="status">{status}</span>\n'
                f'  </div>\n'
                f'  <p class="meta">Última actividad: {relative_time(t["last_mtime"])}</p>\n'
                f'  <div class="artifacts">{arts_html}</div>\n'
                f'</article>'
            )
        cards_html = "\n".join(cards)

    sub = f"{len(tickets)} ticket(s) · actualizado {datetime.now().strftime('%H:%M:%S')}"
    return (INDEX_TEMPLATE
            .replace("__CARDS__", cards_html)
            .replace("__SUB__", sub)
            .replace("__ROOT__", str(root)))


class Handler(http.server.SimpleHTTPRequestHandler):
    server_root: Path = Path.cwd()

    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(self.server_root), **kwargs)

    def do_GET(self):
        if self.path in ("/", "/index.html"):
            try:
                tickets = discover_tickets(self.server_root)
                body = render_index(self.server_root, tickets).encode("utf-8")
            except Exception as e:
                self.send_error(500, f"index error: {e}")
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
            return
        super().do_GET()

    def log_message(self, fmt, *args):
        sys.stderr.write("[log-server] " + (fmt % args) + "\n")


class ThreadingServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=os.environ.get("PA_LOG_DIR", os.getcwd()))
    parser.add_argument("--port", type=int, default=int(os.environ.get("PA_LOG_PORT", "9000")))
    parser.add_argument("--bind", default=os.environ.get("PA_LOG_BIND", "0.0.0.0"))
    args = parser.parse_args()

    root = Path(args.root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    Handler.server_root = root

    with ThreadingServer((args.bind, args.port), Handler) as httpd:
        print(f"[log-server] sirviendo {root} en http://{args.bind}:{args.port}", flush=True)
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("[log-server] adiós.", flush=True)


if __name__ == "__main__":
    main()
