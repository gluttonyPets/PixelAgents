#!/usr/bin/env python3
import argparse
import json
import os
import re
from functools import partial
from http import HTTPStatus
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote


AUTOMATION_DIR = Path(__file__).resolve().parent
DEFAULT_LOG_DIR = Path(os.environ.get(
    "WORKER_LOG_DIR",
    str(AUTOMATION_DIR / "logs"),
)).resolve()
INDEX_TEMPLATE_PATH = AUTOMATION_DIR / "log_index_template.html"


def read_events_payload(ticket_dir: Path) -> dict:
    events_path = ticket_dir / "events.json"

    if not events_path.exists():
        return {}

    try:
        return json.loads(events_path.read_text(encoding="utf-8"))
    except Exception:
        return {}


def summarize_ticket(ticket_dir: Path) -> dict:
    payload = read_events_payload(ticket_dir)
    match = re.match(r"ticket-(\d+)$", ticket_dir.name)
    ticket_id = int(match.group(1)) if match else None
    stats = payload.get("stats") or {}
    events = payload.get("events") or []

    return {
        "ticket_id": payload.get("ticket_id", ticket_id),
        "headline": payload.get("headline") or "(sin titulo)",
        "status": payload.get("status") or "unknown",
        "updated_at": payload.get("updated_at") or ticket_dir.stat().st_mtime,
        "started_at": payload.get("started_at"),
        "event_count": len(events),
        "assistant_messages": stats.get("assistant_messages", 0),
        "tool_calls": stats.get("tool_calls", 0),
        "errors": stats.get("errors", 0),
        "final_result": stats.get("final_result", ""),
        "report_url": f"/{ticket_dir.name}/report.html",
        "live_log_url": f"/{ticket_dir.name}/live.log",
        "events_url": f"/{ticket_dir.name}/events.json",
        "raw_url": f"/{ticket_dir.name}/raw.jsonl",
        "markdown_url": f"/{ticket_dir.name}/report.md",
    }


def summarize_legacy_ticket(log_dir: Path, ticket_id: int) -> dict:
    prefix = f"ticket-{ticket_id}"
    events_path = log_dir / f"{prefix}.events.json"
    payload = {}

    if events_path.exists():
        try:
            payload = json.loads(events_path.read_text(encoding="utf-8"))
        except Exception:
            payload = {}

    stats = payload.get("stats") or {}
    events = payload.get("events") or []

    return {
        "ticket_id": payload.get("ticket_id", ticket_id),
        "headline": payload.get("headline") or "(sin titulo)",
        "status": payload.get("status") or "unknown",
        "updated_at": payload.get("updated_at") or (events_path.stat().st_mtime if events_path.exists() else 0),
        "started_at": payload.get("started_at"),
        "event_count": len(events),
        "assistant_messages": stats.get("assistant_messages", 0),
        "tool_calls": stats.get("tool_calls", 0),
        "errors": stats.get("errors", 0),
        "final_result": stats.get("final_result", ""),
        "report_url": f"/{prefix}.report.html",
        "live_log_url": f"/{prefix}.live.log",
        "events_url": f"/{prefix}.events.json",
        "raw_url": f"/{prefix}.raw.jsonl",
        "markdown_url": f"/{prefix}.report.md",
        "storage_mode": "legacy-flat",
    }


def list_ticket_logs(log_dir: Path) -> list[dict]:
    items = []
    seen_ticket_ids: set[int] = set()

    for entry in log_dir.iterdir():
        if not entry.is_dir() or not re.match(r"ticket-\d+$", entry.name):
            continue

        summary = summarize_ticket(entry)
        if summary.get("ticket_id") is not None:
            seen_ticket_ids.add(int(summary["ticket_id"]))
        items.append(summary)

    legacy_ids: set[int] = set()
    for entry in log_dir.iterdir():
        if entry.is_dir():
            continue

        match = re.match(r"ticket-(\d+)\.", entry.name)
        if not match:
            continue

        legacy_ids.add(int(match.group(1)))

    for ticket_id in sorted(legacy_ids):
        if ticket_id in seen_ticket_ids:
            continue
        items.append(summarize_legacy_ticket(log_dir, ticket_id))

    items.sort(key=lambda item: (item.get("updated_at") or 0, item.get("ticket_id") or 0), reverse=True)
    return items


def list_root_logs(log_dir: Path) -> list[dict]:
    items = []

    for entry in log_dir.iterdir():
        if entry.is_dir():
            continue

        if re.match(r"ticket-\d+\.", entry.name):
            continue

        if entry.suffix.lower() not in {".log", ".txt", ".json", ".jsonl", ".md", ".html"}:
            continue

        items.append({
            "name": entry.name,
            "size": entry.stat().st_size,
            "updated_at": entry.stat().st_mtime,
            "url": f"/{entry.name}",
        })

    items.sort(key=lambda item: (item.get("updated_at") or 0, item.get("name") or ""), reverse=True)
    return items


def build_index_payload(log_dir: Path) -> dict:
    return {
        "generated_at": os.path.getmtime(log_dir),
        "tickets": list_ticket_logs(log_dir),
        "root_logs": list_root_logs(log_dir),
    }


class LogRequestHandler(SimpleHTTPRequestHandler):
    server_version = "PixelAgentsLogServer/1.0"

    def __init__(self, *args, log_dir: Path, **kwargs):
        self.log_dir = log_dir
        super().__init__(*args, directory=str(log_dir), **kwargs)

    def do_GET(self):
        path = unquote(self.path.split("?", 1)[0])

        if path == "/":
            return self.serve_home()

        if path == "/api/logs":
            return self.serve_logs_api()

        if re.fullmatch(r"/ticket-\d+", path):
            self.send_response(HTTPStatus.MOVED_PERMANENTLY)
            self.send_header("Location", path + "/report.html")
            self.end_headers()
            return

        return super().do_GET()

    def list_directory(self, path):
        self.send_error(HTTPStatus.NOT_FOUND, "Directory listing disabled")
        return None

    def serve_home(self):
        if not INDEX_TEMPLATE_PATH.exists():
            self.send_error(HTTPStatus.INTERNAL_SERVER_ERROR, "Missing index template")
            return

        content = INDEX_TEMPLATE_PATH.read_text(encoding="utf-8")
        body = content.replace("{{API_URL}}", "/api/logs")
        encoded = body.encode("utf-8")

        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def serve_logs_api(self):
        payload = build_index_payload(self.log_dir)
        encoded = json.dumps(payload, ensure_ascii=False).encode("utf-8")

        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)


def parse_args():
    parser = argparse.ArgumentParser(description="Serve PixelAgents logs with a simple HTML dashboard.")
    parser.add_argument("--bind", default="0.0.0.0", help="Address to bind")
    parser.add_argument("--port", type=int, default=9000, help="Port to listen on")
    parser.add_argument("--log-dir", default=str(DEFAULT_LOG_DIR), help="Logs root directory")
    return parser.parse_args()


def main():
    args = parse_args()
    log_dir = Path(args.log_dir).resolve()
    log_dir.mkdir(parents=True, exist_ok=True)

    handler = partial(LogRequestHandler, log_dir=log_dir)
    with ThreadingHTTPServer((args.bind, args.port), handler) as server:
        print(f"[log-server] Serving {log_dir} on http://{args.bind}:{args.port}", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
