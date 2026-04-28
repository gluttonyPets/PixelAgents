#!/usr/bin/env python3
import asyncio
import html
import inspect
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Any

from claude_agent_sdk import ClaudeAgentOptions, query


# =========================
# Rutas base
# =========================

AUTOMATION_DIR = Path(__file__).resolve().parent
TEMPLATE_PATH = AUTOMATION_DIR / "report_template.html"
CSS_SOURCE_PATH = AUTOMATION_DIR / "report.css"
JS_SOURCE_PATH = AUTOMATION_DIR / "report.js"


# =========================
# Configuración
# =========================

LEANTIME_API_URL = os.environ.get(
    "LEANTIME_API_URL",
    "http://127.0.0.1:8090/api/jsonrpc",
)

LEANTIME_API_KEY = os.environ["LEANTIME_API_KEY"]

PROJECT_ID = int(os.environ.get("LEANTIME_PROJECT_ID", "3"))

REPO_PATH = os.environ.get(
    "PIXELAGENTS_REPO",
    "/home/debian/Proyectos/PixelAgents",
)

CLAUDE_AGENT = os.environ.get("CLAUDE_AGENT", "coordinator")

POLL_SECONDS = int(os.environ.get("POLL_SECONDS", "30"))

READY_STATUS_ID = int(os.environ["READY_STATUS_ID"])
IN_PROGRESS_STATUS_ID = int(os.environ["IN_PROGRESS_STATUS_ID"])
REVIEW_STATUS_ID = int(os.environ.get("REVIEW_STATUS_ID", "5"))

STATE_FILE = Path(os.environ.get(
    "WORKER_STATE_FILE",
    "/home/debian/.config/pixelagents/processed_ready_tasks.json",
))

LOG_DIR = Path(os.environ.get(
    "WORKER_LOG_DIR",
    "/home/debian/Proyectos/PixelAgents/automation/logs",
))

CLAUDE_PERMISSION_MODE = os.environ.get(
    "CLAUDE_PERMISSION_MODE",
    "bypassPermissions",
)

CLAUDE_TIMEOUT_SECONDS = int(os.environ.get(
    "CLAUDE_TIMEOUT_SECONDS",
    str(60 * 60 * 2),
))

CLAUDE_MAX_TURNS = int(os.environ.get("CLAUDE_MAX_TURNS", "80"))
CLAUDE_MAX_BUDGET_USD = float(os.environ.get("CLAUDE_MAX_BUDGET_USD", "5.0"))

LEANTIME_MCP_WRAPPER = os.environ.get(
    "LEANTIME_MCP_WRAPPER",
    "/home/debian/.config/pixelagents/leantime-mcp-wrapper.sh",
)

WORKER_LOG_RAW_EVENTS = os.environ.get("WORKER_LOG_RAW_EVENTS", "0") == "1"
WORKER_LOG_PROMPT = os.environ.get("WORKER_LOG_PROMPT", "0") == "1"

MAX_TOOL_INPUT_CHARS = int(os.environ.get("MAX_TOOL_INPUT_CHARS", "1200"))
MAX_TOOL_RESULT_CHARS = int(os.environ.get("MAX_TOOL_RESULT_CHARS", "2200"))
MAX_ASSISTANT_TEXT_CHARS = int(os.environ.get("MAX_ASSISTANT_TEXT_CHARS", "9000"))

CLAUDE_SESSION_MARKER_PREFIX = "[CLAUDE_SESSION]"


# =========================
# Utilidades
# =========================

def resolve_runtime_home() -> str:
    return os.environ.get("HOME", "/home/debian")


def build_runtime_path(home: str | None = None) -> str:
    home = home or resolve_runtime_home()
    current_path = os.environ.get("PATH", "")

    preferred = [
        f"{home}/.local/bin",
        f"{home}/.npm-global/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
    ]

    path_parts: list[str] = []
    seen: set[str] = set()

    for entry in preferred + current_path.split(os.pathsep):
        cleaned = entry.strip()
        if not cleaned or cleaned in seen:
            continue
        seen.add(cleaned)
        path_parts.append(cleaned)

    return os.pathsep.join(path_parts)


def resolve_claude_bin() -> str:
    configured = os.environ.get("CLAUDE_BIN")
    if configured:
        return configured

    home = resolve_runtime_home()
    search_path = build_runtime_path(home)

    found = shutil.which("claude", path=search_path)
    if found:
        return found

    candidates = [
        Path(home) / ".local/bin/claude",
        Path(home) / ".npm-global/bin/claude",
        Path("/usr/local/bin/claude"),
        Path("/usr/bin/claude"),
    ]

    for candidate in candidates:
        if candidate.exists():
            return str(candidate)

    return "claude"


CLAUDE_BIN = resolve_claude_bin()

def truncate(value: Any, limit: int) -> str:
    if value is None:
        return ""

    text = str(value)

    if len(text) <= limit:
        return text

    omitted = len(text) - limit
    return text[:limit] + f"\n\n[… {omitted} caracteres omitidos …]"


def compact_json(value: Any, limit: int = 1200) -> str:
    try:
        rendered = json.dumps(value, ensure_ascii=False, indent=2, default=str)
    except Exception:
        rendered = str(value)

    return truncate(rendered, limit)


def object_to_dict(obj: Any) -> dict[str, Any]:
    if obj is None:
        return {}

    if isinstance(obj, dict):
        return obj

    if hasattr(obj, "model_dump"):
        try:
            dumped = obj.model_dump()
            if isinstance(dumped, dict):
                return dumped
        except Exception:
            pass

    if hasattr(obj, "__dict__"):
        try:
            return dict(obj.__dict__)
        except Exception:
            pass

    return {"repr": repr(obj)}


def get_attr_or_key(obj: Any, name: str, default: Any = None) -> Any:
    if isinstance(obj, dict):
        return obj.get(name, default)

    return getattr(obj, name, default)


def clean_text(text: Any) -> str:
    if text is None:
        return ""

    text = str(text).strip()

    if text.startswith("ThinkingBlock("):
        return ""

    if text.startswith("ToolResultBlock("):
        return parse_tool_result_string(text)

    return text


def is_thinking_block(block: Any) -> bool:
    cls = type(block).__name__

    if cls == "ThinkingBlock":
        return True

    if hasattr(block, "thinking"):
        return True

    if isinstance(block, str) and block.startswith("ThinkingBlock("):
        return True

    return False


def parse_tool_result_string(text: str) -> str:
    is_error = "is_error=True" in text

    tool_id_match = re.search(r"tool_use_id='([^']+)'", text)
    tool_id = tool_id_match.group(1) if tool_id_match else "tool"

    content_match = re.search(r"content=(['\"])(.*)\1,\s*is_error=", text, re.DOTALL)

    if content_match:
        content = content_match.group(2)
        try:
            content = bytes(content, "utf-8").decode("unicode_escape")
        except Exception:
            pass
    else:
        content = text

    label = "ERROR" if is_error else "OK"
    return f"{label} {tool_id}\n{content}"


def atomic_write_text(path: Path, content: str) -> None:
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(content, encoding="utf-8")
    tmp.replace(path)


def atomic_write_json(path: Path, payload: Any) -> None:
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2, default=str),
        encoding="utf-8",
    )
    tmp.replace(path)


def raw_log_event(raw_log, message: Any) -> None:
    if raw_log is None:
        return

    try:
        if hasattr(message, "model_dump"):
            payload = message.model_dump()
        else:
            payload = object_to_dict(message)

        raw_log.write(json.dumps({
            "type": type(message).__name__,
            "payload": payload,
        }, ensure_ascii=False, default=str) + "\n")
        raw_log.flush()

    except Exception:
        raw_log.write(json.dumps({
            "type": type(message).__name__,
            "repr": repr(message),
        }, ensure_ascii=False) + "\n")
        raw_log.flush()


# =========================
# Cliente JSON-RPC Leantime
# =========================

def rpc(method: str, params: dict[str, Any] | None = None):
    payload = {
        "jsonrpc": "2.0",
        "id": 1,
        "method": method,
        "params": params or {},
    }

    data = json.dumps(payload).encode("utf-8")

    req = urllib.request.Request(
        LEANTIME_API_URL,
        data=data,
        headers={
            "Content-Type": "application/json",
            "x-api-key": LEANTIME_API_KEY,
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            body = resp.read().decode("utf-8")
            result = json.loads(body)

    except urllib.error.HTTPError as e:
        error_body = e.read().decode("utf-8", errors="ignore")
        print(f"[ERROR] HTTP {e.code}: {error_body}", flush=True)
        return None

    except Exception as e:
        print(f"[ERROR] RPC failed: {e}", flush=True)
        return None

    if "error" in result:
        print(f"[ERROR] RPC error for {method}: {result['error']}", flush=True)
        return None

    return result.get("result")


# =========================
# Estado local del worker
# =========================

def load_worker_state() -> dict[str, Any]:
    if not STATE_FILE.exists():
        return {
            "processed_ready_tasks": [],
            "review_comment_markers": {},
            "ticket_sessions": {},
        }

    try:
        raw = json.loads(STATE_FILE.read_text(encoding="utf-8"))
    except Exception:
        raw = None

    if isinstance(raw, list):
        return {
            "processed_ready_tasks": raw,
            "review_comment_markers": {},
            "ticket_sessions": {},
        }

    if isinstance(raw, dict):
        processed = raw.get("processed_ready_tasks", [])
        review_markers = raw.get("review_comment_markers", {})
        ticket_sessions = raw.get("ticket_sessions", {})

        if not isinstance(processed, list):
            processed = []

        if not isinstance(review_markers, dict):
            review_markers = {}

        if not isinstance(ticket_sessions, dict):
            ticket_sessions = {}

        return {
            "processed_ready_tasks": processed,
            "review_comment_markers": review_markers,
            "ticket_sessions": ticket_sessions,
        }

    return {
        "processed_ready_tasks": [],
        "review_comment_markers": {},
        "ticket_sessions": {},
    }


def save_worker_state(state: dict[str, Any]) -> None:
    payload = {
        "processed_ready_tasks": sorted({
            int(ticket_id)
            for ticket_id in state.get("processed_ready_tasks", [])
        }),
        "review_comment_markers": {
            str(ticket_id): str(marker)
            for ticket_id, marker in state.get("review_comment_markers", {}).items()
            if marker
        },
        "ticket_sessions": {
            str(ticket_id): str(session_id)
            for ticket_id, session_id in state.get("ticket_sessions", {}).items()
            if session_id
        },
    }

    STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    STATE_FILE.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


# =========================
# Leantime helpers
# =========================

def get_all_tasks() -> list[dict[str, Any]]:
    result = rpc("leantime.rpc.tickets.getAll", {})
    if not result:
        return []
    return result


def get_ready_tasks() -> list[dict[str, Any]]:
    return get_tasks_by_status({READY_STATUS_ID})


def get_review_tasks() -> list[dict[str, Any]]:
    return get_tasks_by_status({REVIEW_STATUS_ID})


def get_tasks_by_status(status_ids: set[int]) -> list[dict[str, Any]]:
    tasks = get_all_tasks()
    matching = []

    for task in tasks:
        try:
            task_project_id = int(task.get("projectId", 0))
            task_status_id = int(task.get("status", -1))
        except Exception:
            continue

        if task_project_id != PROJECT_ID:
            continue

        if task_status_id in status_ids:
            matching.append(task)

    return matching


def get_ticket(ticket_id: int) -> dict[str, Any] | None:
    result = rpc("leantime.rpc.tickets.getTicket", {
        "id": int(ticket_id),
    })
    if not isinstance(result, dict):
        return None
    return result


def update_ticket_status(ticket_id: int, status_id: int):
    ticket = get_ticket(ticket_id)
    if not ticket:
        print(
            f"[ERROR] Could not read ticket {ticket_id} before updating status.",
            flush=True,
        )
        return None

    update_values = dict(ticket)
    update_values["id"] = int(ticket_id)
    update_values["status"] = str(status_id)

    # Leantime's update RPC expects the full ticket payload. Sending only the
    # status risks clearing fields such as description.
    return rpc("leantime.rpc.tickets.updateTicket", update_values)


def normalize_comments_payload(result: Any) -> list[dict[str, Any]]:
    if isinstance(result, list):
        return [item for item in result if isinstance(item, dict)]

    if isinstance(result, dict):
        for key in ("comments", "result", "data", "rows"):
            value = result.get(key)
            if isinstance(value, list):
                return [item for item in value if isinstance(item, dict)]

    return []


def get_ticket_comments(ticket_id: int) -> list[dict[str, Any]]:
    candidate_calls = [
        ("leantime.rpc.comments.getComments", {
            "moduleId": int(ticket_id),
            "commentModule": "tickets",
        }),
        ("leantime.rpc.comments.getComments", {
            "moduleId": str(ticket_id),
            "commentModule": "tickets",
        }),
        ("leantime.rpc.comments.getAll", {
            "moduleId": int(ticket_id),
            "commentModule": "tickets",
        }),
        ("leantime.rpc.comments.getAll", {
            "moduleId": str(ticket_id),
            "commentModule": "tickets",
        }),
    ]

    for method, params in candidate_calls:
        result = rpc(method, params)
        comments = normalize_comments_payload(result)
        if comments:
            return comments

    return []


def add_ticket_comment(ticket_id: int, comment: str) -> Any:
    return rpc("leantime.rpc.comments.addComment", {
        "moduleId": int(ticket_id),
        "commentModule": "tickets",
        "comment": comment,
        "commentParent": "",
    })


def is_session_marker_comment(comment: dict[str, Any]) -> bool:
    body = str(
        comment.get("comment")
        or comment.get("content")
        or comment.get("description")
        or ""
    ).strip()
    return body.startswith(CLAUDE_SESSION_MARKER_PREFIX)


def extract_session_id_from_marker_comment(comment: dict[str, Any]) -> str:
    body = str(
        comment.get("comment")
        or comment.get("content")
        or comment.get("description")
        or ""
    ).strip()
    match = re.search(r"session_id=([A-Za-z0-9._:-]+)", body)
    return match.group(1) if match else ""


def get_review_relevant_comments(ticket_id: int) -> list[dict[str, Any]]:
    return [
        comment
        for comment in get_ticket_comments(ticket_id)
        if not is_session_marker_comment(comment)
    ]


def get_ticket_session_id_from_comments(ticket_id: int) -> str:
    marker_comments = [
        comment
        for comment in get_ticket_comments(ticket_id)
        if is_session_marker_comment(comment)
    ]

    if not marker_comments:
        return ""

    latest_marker = sort_comments(marker_comments)[-1]
    return extract_session_id_from_marker_comment(latest_marker)


def sync_ticket_session_marker(ticket_id: int, session_id: str) -> None:
    if not session_id:
        return

    current_session_id = get_ticket_session_id_from_comments(ticket_id)
    if current_session_id == session_id:
        return

    marker_comment = (
        f"{CLAUDE_SESSION_MARKER_PREFIX} session_id={session_id}\n"
        "Etiqueta técnica para reanudar o reconstruir el contexto de Claude."
    )
    add_ticket_comment(ticket_id, marker_comment)


def comment_sort_key(comment: dict[str, Any]) -> tuple[int, str, str]:
    raw_id = (
        comment.get("id")
        or comment.get("commentId")
        or comment.get("pk")
        or 0
    )

    try:
        comment_id = int(raw_id)
    except Exception:
        comment_id = 0

    ts = str(
        comment.get("dateModified")
        or comment.get("dateCreated")
        or comment.get("createdAt")
        or comment.get("modifiedAt")
        or ""
    )

    parent = str(comment.get("commentParent") or "")
    return (comment_id, ts, parent)


def comment_signature(comment: dict[str, Any]) -> str:
    comment_id, ts, parent = comment_sort_key(comment)
    preview = str(
        comment.get("comment")
        or comment.get("content")
        or comment.get("description")
        or ""
    ).strip()
    preview = re.sub(r"\s+", " ", preview)[:120]
    return f"{comment_id}|{ts}|{parent}|{preview}"


def sort_comments(comments: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return sorted(comments, key=comment_sort_key)


def latest_comment_signature(comments: list[dict[str, Any]]) -> str:
    if not comments:
        return ""
    return comment_signature(sort_comments(comments)[-1])


def comments_after_signature(
    comments: list[dict[str, Any]],
    signature: str,
) -> list[dict[str, Any]]:
    ordered = sort_comments(comments)

    if not signature:
        return ordered

    seen_signature = False
    pending: list[dict[str, Any]] = []

    for comment in ordered:
        if seen_signature:
            pending.append(comment)
            continue

        if comment_signature(comment) == signature:
            seen_signature = True

    if seen_signature:
        return pending

    return ordered


def comment_text(comment: dict[str, Any]) -> str:
    author = (
        comment.get("fullName")
        or comment.get("name")
        or comment.get("user")
        or comment.get("author")
        or "desconocido"
    )
    body = str(
        comment.get("comment")
        or comment.get("content")
        or comment.get("description")
        or ""
    ).strip()
    body = body or "(sin texto)"
    return f"Autor: {author}\n{body}"


def ensure_agent_users_provisioned() -> bool:
    home = Path(resolve_runtime_home())
    config_dir = Path(os.environ.get(
        "PIXELAGENTS_CONFIG_DIR",
        home / ".config/pixelagents",
    ))
    agents_file = Path(os.environ.get(
        "PIXELAGENTS_AGENT_USERS_FILE",
        config_dir / "agent_users.json",
    ))

    if agents_file.exists():
        try:
            mapping = json.loads(agents_file.read_text(encoding="utf-8"))
            if isinstance(mapping, dict) and mapping:
                return True
        except Exception:
            pass

    provision_script = Path(REPO_PATH) / "tools" / "provision_leantime_agents.py"
    if not provision_script.exists():
        print(
            f"[ERROR] Missing provision script: {provision_script}",
            flush=True,
        )
        return False

    env = os.environ.copy()
    env["HOME"] = resolve_runtime_home()
    env["PATH"] = build_runtime_path(env["HOME"])

    try:
        result = subprocess.run(
            [sys.executable, str(provision_script)],
            cwd=REPO_PATH,
            env=env,
            capture_output=True,
            text=True,
            timeout=60,
            check=False,
        )
    except Exception as exc:
        print(
            f"[ERROR] Failed provisioning Leantime agent users: {exc}",
            flush=True,
        )
        return False

    if result.stdout.strip():
        print(result.stdout.strip(), flush=True)
    if result.stderr.strip():
        print(result.stderr.strip(), flush=True)

    if result.returncode != 0:
        print(
            f"[ERROR] provision_leantime_agents.py exited with code {result.returncode}",
            flush=True,
        )
        return False

    return agents_file.exists()


# =========================
# Prompt
# =========================

def build_prompt(
    task: dict[str, Any],
    trigger: str = "ready",
    review_comments: list[dict[str, Any]] | None = None,
    previous_session_id: str = "",
) -> str:
    ticket_id = int(task["id"])
    headline = task.get("headline", "")
    description = task.get("description", "")
    review_comments = review_comments or []

    if trigger == "review_feedback":
        trigger_context = "\n".join([
            "- Esta tarea ya estaba en Review.",
            "- El worker ha detectado comentarios nuevos del humano en la tarea o subtarea.",
            "- Debes tratar esos comentarios como feedback correctivo y reanudar el trabajo.",
        ])
        trigger_rules = "\n".join([
            "- Primero resume qué correcciones pide el humano en los comentarios nuevos.",
            "- Si la tarea o subtarea estaba en Review, devuélvela a In Progress mientras aplicas los cambios.",
            "- Responde en comentarios de Leantime indicando qué has corregido o qué bloqueo impide corregirlo.",
            "- Cuando acabes las correcciones y validaciones, vuelve a dejar la tarea en Review.",
        ])
    else:
        trigger_context = "\n".join([
            "- Esta tarea fue movida a Ready por el usuario humano.",
            "- El worker la ha detectado y debe comenzar el flujo de trabajo.",
        ])
        trigger_rules = ""

    comments_block = ""
    if review_comments:
        rendered_comments = "\n\n".join(
            f"Comentario {idx + 1}\n{comment_text(comment)}"
            for idx, comment in enumerate(review_comments[-5:])
        )
        comments_block = f"\n\nComentarios nuevos a procesar:\n{rendered_comments}"

    session_block = ""
    if previous_session_id:
        session_block = (
            "\n\nContexto de conversación Claude previo:\n"
            f"- Session ID anterior: {previous_session_id}\n"
            "- Usa este identificador para referenciar el trabajo previo, su plan y sus decisiones."
        )

    return f"""
Actúa como el agente principal `coordinator` del proyecto PixelAgents.

Trabaja sobre la tarea Leantime ID {ticket_id} del proyecto PixelAgents.

Título:
{headline}

Descripción:
{description}

Contexto operativo:
{trigger_context}
- Usa Leantime como fuente de verdad.
- Usa el MCP de Leantime siempre que esté disponible.
- Si MCP falla, usa ./tools/leantime.sh como fallback.
{comments_block}
{session_block}

Reglas obligatorias:
- Primero escribe en la salida: INICIANDO TAREA {ticket_id}
- Antes de modificar código, asegúrate de estar en una rama de trabajo segura y dedicada a esa tarea unicamente.
- Si estás en develop, crea una rama con formato:
  feature/leantime-{ticket_id}-descripcion-corta
- Si estás en master o main, detente y mueve la tarea a Blocked.
- Crea subtareas técnicas en Leantime si el trabajo no es trivial.
- Divide el trabajo entre agentes lógicos cuando aplique:
  - coordinator
  - backend-implementer
  - frontend-implementer
  - test-runner
  - code-reviewer
- Mueve las subtareas autónomamente según avance.
- Mantén la tarea principal sincronizada con el avance de las subtareas.
- Documenta en Leantime todo lo que hagas:
  - plan técnico
  - subtareas creadas
  - archivos tocados
  - comandos ejecutados
  - resultados de tests/lint/build
  - riesgos
  - decisiones pendientes
  - siguiente paso
- No marques la tarea principal como Done sin aprobación humana.
- Puedes hacer commits automáticamente SOLO en ramas feature/*, fix/* o hotfix/*.
- Debes subir automáticamente la rama de trabajo para que el usuario pueda verla desde otro equipo.
- Para hacer commit y push usa SIEMPRE:
  ./tools/git_safe_commit_push.sh {ticket_id} "Leantime #{ticket_id}: resumen corto del cambio"
- Nunca hagas push directo a master, main o develop.
- Nunca hagas merge a develop sin aprobación humana.
- Nunca hagas merge a master sin aprobación humana.
- Recuerda:
  - master despliega a producción
  - develop despliega a preproducción
- Si el trabajo queda bloqueado, mueve la tarea a Blocked y explica el motivo en Leantime.
- Si terminas implementación, validaciones y revisión interna, deja la tarea principal en Review.
- Al terminar, deja un resumen final en la salida y en Leantime.
{trigger_rules}

Empieza ahora.
""".strip()


# =========================
# Eventos legibles
# =========================

@dataclass
class RunStats:
    assistant_messages: int = 0
    tool_calls: int = 0
    tool_results: int = 0
    errors: int = 0
    hidden_thinking_blocks: int = 0
    result_cost_usd: float = 0.0
    num_turns: int = 0
    final_result: str = ""


@dataclass
class HumanEvent:
    kind: str
    title: str
    body: str = ""
    status: str = "info"
    meta: dict[str, Any] = field(default_factory=dict)
    ts: float = field(default_factory=time.time)


def markdown_escape_fence(text: str) -> str:
    return text.replace("```", "`\u200b``")


class LiveReportLogger:
    def __init__(self, ticket_id: int, headline: str, prompt: str):
        LOG_DIR.mkdir(parents=True, exist_ok=True)

        self.ticket_id = ticket_id
        self.headline = headline
        self.prompt = prompt

        self.ticket_dir = LOG_DIR / f"ticket-{ticket_id}"
        self.ticket_dir.mkdir(parents=True, exist_ok=True)

        self.live_log_path = self.ticket_dir / "live.log"
        self.report_md_path = self.ticket_dir / "report.md"
        self.report_html_path = self.ticket_dir / "report.html"
        self.events_json_path = self.ticket_dir / "events.json"
        self.raw_log_path = self.ticket_dir / "raw.jsonl"

        self.live_file = self.live_log_path.open("w", encoding="utf-8")
        self.raw_file = self.raw_log_path.open("w", encoding="utf-8") if WORKER_LOG_RAW_EVENTS else None

        self.stats = RunStats()
        self.events: list[HumanEvent] = []
        self.started_at = time.time()
        self.status = "running"
        self.session_id = ""

        self.tool_id_to_name: dict[str, str] = {}

        self.copy_static_assets()
        self.write_live_header()
        self.write_static_html()
        self.write_events_json()
        self.write_markdown()

    def copy_static_assets(self):
        if CSS_SOURCE_PATH.exists():
            shutil.copyfile(CSS_SOURCE_PATH, self.ticket_dir / "report.css")

        if JS_SOURCE_PATH.exists():
            shutil.copyfile(JS_SOURCE_PATH, self.ticket_dir / "report.js")

    def close(self):
        self.write_events_json()
        self.write_markdown()
        if self.raw_file:
            self.raw_file.close()
        self.live_file.close()

    def write_live(self, text: str = ""):
        print(text, flush=True)
        self.live_file.write(text + "\n")
        self.live_file.flush()

    def write_live_header(self):
        self.write_live("=" * 80)
        self.write_live(f"PixelAgents · Ticket #{self.ticket_id}")
        self.write_live(f"Título: {self.headline}")
        self.write_live(f"Repo: {REPO_PATH}")
        self.write_live(f"Agente: {CLAUDE_AGENT}")
        self.write_live(f"Permisos: {CLAUDE_PERMISSION_MODE}")
        self.write_live(f"Estado: {self.status}")
        self.write_live("=" * 80)

        if WORKER_LOG_PROMPT:
            self.write_live("")
            self.write_live("PROMPT ENVIADO")
            self.write_live("-" * 80)
            self.write_live(self.prompt)
            self.write_live("-" * 80)

    def write_static_html(self):
        if not TEMPLATE_PATH.exists():
            raise FileNotFoundError(f"No existe la plantilla HTML: {TEMPLATE_PATH}")

        title = f"Ticket #{self.ticket_id} · {self.headline}"

        content = TEMPLATE_PATH.read_text(encoding="utf-8")
        content = content.replace("{{TITLE}}", html.escape(title))
        content = content.replace("{{EVENTS_FILE}}", html.escape(self.events_json_path.name))
        content = content.replace("{{REPO_PATH}}", html.escape(REPO_PATH))
        content = content.replace("{{CLAUDE_AGENT}}", html.escape(CLAUDE_AGENT))

        atomic_write_text(self.report_html_path, content)

    def write_events_json(self):
        payload = {
            "ticket_id": self.ticket_id,
            "headline": self.headline,
            "status": self.status,
            "started_at": self.started_at,
            "updated_at": time.time(),
            "stats": asdict(self.stats),
            "events": [asdict(ev) for ev in self.events],
        }
        atomic_write_json(self.events_json_path, payload)

    def write_markdown(self):
        lines = [
            f"# Ticket #{self.ticket_id} · {self.headline}",
            "",
            "## Vista principal",
            "",
        ]

        for ev in self.events:
            if ev.kind not in {"assistant", "result"}:
                continue

            icon = "💬" if ev.kind == "assistant" else "🏁"
            lines.extend([
                f"### {icon} {ev.title}",
                "",
                ev.body,
                "",
            ])

        lines.extend([
            "## Log técnico",
            "",
        ])

        for ev in self.events:
            if ev.kind in {"assistant", "result"}:
                continue

            icon = {
                "tool": "🔧",
                "tool_result": "✅" if ev.status != "error" else "❌",
                "system": "⚙️",
            }.get(ev.kind, "•")

            tool = ev.meta.get("tool") or ev.title
            lines.extend([
                f"### {icon} {tool}",
                "",
                "```text",
                markdown_escape_fence(ev.body),
                "```",
                "",
            ])

        lines.extend([
            "## Resumen",
            "",
            f"- **Mensajes Claude:** {self.stats.assistant_messages}",
            f"- **Herramientas llamadas:** {self.stats.tool_calls}",
            f"- **Resultados herramientas:** {self.stats.tool_results}",
            f"- **Errores:** {self.stats.errors}",
            f"- **Thinking oculto:** {self.stats.hidden_thinking_blocks}",
            f"- **Turnos:** {self.stats.num_turns}",
            f"- **Coste USD:** {self.stats.result_cost_usd:.6f}",
            "",
        ])

        atomic_write_text(self.report_md_path, "\n".join(lines))

    def raw(self, message: Any):
        raw_log_event(self.raw_file, message)

    def resolve_tool_name(self, event: HumanEvent) -> HumanEvent:
        if event.kind == "tool":
            tool_id = event.meta.get("tool_use_id")
            tool_name = event.meta.get("tool") or event.title

            if tool_id:
                self.tool_id_to_name[str(tool_id)] = str(tool_name)

            return event

        if event.kind == "tool_result":
            tool_id = event.meta.get("tool_use_id") or event.title
            tool_name = self.tool_id_to_name.get(str(tool_id))

            if tool_name:
                event.meta["tool"] = tool_name
                event.title = tool_name

            return event

        return event

    def event(self, event: HumanEvent):
        event = self.resolve_tool_name(event)

        session_id = str(event.meta.get("session_id", "") or "")
        if session_id:
            self.session_id = session_id

        if event.kind == "assistant":
            self.stats.assistant_messages += 1
        elif event.kind == "tool":
            self.stats.tool_calls += 1
        elif event.kind == "tool_result":
            self.stats.tool_results += 1
            if event.status == "error":
                self.stats.errors += 1
        elif event.kind == "thinking":
            self.stats.hidden_thinking_blocks += 1
            return

        if not event.body.strip():
            return

        self.events.append(event)

        self.write_live("")
        self.write_live(f"[{event.kind.upper()}] {event.title}")
        self.write_live("-" * 80)
        self.write_live(truncate(event.body, 4000))
        self.write_live("-" * 80)

        self.write_events_json()
        self.write_markdown()

    def set_result_stats(self, message: Any):
        data = object_to_dict(message)

        cost = data.get("total_cost_usd")
        if cost is not None:
            try:
                self.stats.result_cost_usd = float(cost)
            except Exception:
                pass

        turns = data.get("num_turns")
        if turns is not None:
            try:
                self.stats.num_turns = int(turns)
            except Exception:
                pass

        result = data.get("result")
        if result:
            self.stats.final_result = str(result)

    def finish(self, ok: bool):
        self.status = "done" if ok else "error"

        elapsed = int(time.time() - self.started_at)

        summary = HumanEvent(
            kind="result",
            title="Resumen de ejecución",
            body=(
                f"Estado: {'OK' if ok else 'ERROR'}\n"
                f"Duración: {elapsed}s\n"
                f"Mensajes Claude: {self.stats.assistant_messages}\n"
                f"Herramientas llamadas: {self.stats.tool_calls}\n"
                f"Resultados herramientas: {self.stats.tool_results}\n"
                f"Errores: {self.stats.errors}\n"
                f"Thinking oculto: {self.stats.hidden_thinking_blocks}\n"
                f"Turnos: {self.stats.num_turns}\n"
                f"Coste USD: {self.stats.result_cost_usd:.6f}\n"
            ),
            status="ok" if ok else "error",
        )

        self.events.append(summary)

        self.write_live("")
        self.write_live("[RESULT] Resumen de ejecución")
        self.write_live(summary.body)

        self.write_events_json()
        self.write_markdown()


def block_to_human_events(block: Any) -> list[HumanEvent]:
    events: list[HumanEvent] = []

    if is_thinking_block(block):
        return [HumanEvent(kind="thinking", title="thinking hidden")]

    text = get_attr_or_key(block, "text", None)
    if text:
        cleaned = clean_text(text)
        if cleaned:
            events.append(HumanEvent(
                kind="assistant",
                title="Claude",
                body=truncate(cleaned, MAX_ASSISTANT_TEXT_CHARS),
            ))
        return events

    name = get_attr_or_key(block, "name", None)
    tool_input = get_attr_or_key(block, "input", None)
    tool_id = get_attr_or_key(block, "id", None)

    if name is not None:
        events.append(HumanEvent(
            kind="tool",
            title=str(name),
            body=compact_json(tool_input, MAX_TOOL_INPUT_CHARS),
            meta={
                "tool": str(name),
                "tool_use_id": str(tool_id) if tool_id else "",
            },
        ))
        return events

    tool_use_id = get_attr_or_key(block, "tool_use_id", None)
    if tool_use_id is not None:
        is_error = bool(get_attr_or_key(block, "is_error", False))
        content = get_attr_or_key(block, "content", "")

        events.append(HumanEvent(
            kind="tool_result",
            title=str(tool_use_id),
            body=truncate(content, MAX_TOOL_RESULT_CHARS),
            status="error" if is_error else "ok",
            meta={
                "tool_use_id": str(tool_use_id),
            },
        ))
        return events

    if isinstance(block, str):
        if block.startswith("ThinkingBlock("):
            return [HumanEvent(kind="thinking", title="thinking hidden")]

        if block.startswith("ToolResultBlock("):
            parsed = parse_tool_result_string(block)
            is_error = parsed.startswith("ERROR")
            events.append(HumanEvent(
                kind="tool_result",
                title="tool",
                body=truncate(parsed, MAX_TOOL_RESULT_CHARS),
                status="error" if is_error else "ok",
                meta={
                    "tool": "tool_result",
                },
            ))
            return events

        if block.startswith("ToolUseBlock("):
            tool_name_match = re.search(r"name='([^']+)'", block)
            tool_name = tool_name_match.group(1) if tool_name_match else "tool"

            tool_id_match = re.search(r"id='([^']+)'", block)
            tool_id = tool_id_match.group(1) if tool_id_match else ""

            events.append(HumanEvent(
                kind="tool",
                title=tool_name,
                body=truncate(block, MAX_TOOL_INPUT_CHARS),
                meta={
                    "tool": tool_name,
                    "tool_use_id": tool_id,
                },
            ))
            return events

        cleaned = clean_text(block)
        if cleaned:
            events.append(HumanEvent(
                kind="assistant",
                title="Claude",
                body=truncate(cleaned, MAX_ASSISTANT_TEXT_CHARS),
            ))
            return events

    return events


def sdk_message_to_events(message: Any) -> list[HumanEvent]:
    cls = type(message).__name__
    data = object_to_dict(message)

    if cls == "SystemMessage":
        nested = data.get("data") or {}
        if not isinstance(nested, dict):
            nested = {}

        if data.get("subtype") == "init" or nested.get("subtype") == "init":
            mcp_servers = nested.get("mcp_servers", [])
            mcp_lines = []

            for server in mcp_servers:
                if isinstance(server, dict):
                    mcp_lines.append(
                        f"- {server.get('name', 'unknown')}: {server.get('status', 'unknown')}"
                    )

            body = "\n".join([
                f"Model: {nested.get('model', 'unknown')}",
                f"CWD: {nested.get('cwd', '')}",
                f"Session: {nested.get('session_id', '')}",
                f"Permission mode: {nested.get('permissionMode', '')}",
                "MCP servers:",
                "\n".join(mcp_lines) if mcp_lines else "- none",
            ])

            return [HumanEvent(
                kind="system",
                title="Inicio Claude",
                body=body,
                meta={
                    "tool": "system",
                    "session_id": str(nested.get("session_id", "") or ""),
                },
            )]

        return []

    if cls == "RateLimitEvent":
        return []

    if cls == "StreamEvent":
        return []

    if cls == "AssistantMessage":
        content = get_attr_or_key(message, "content", [])

        if isinstance(content, str):
            cleaned = clean_text(content)
            return [HumanEvent(
                kind="assistant",
                title="Claude",
                body=truncate(cleaned, MAX_ASSISTANT_TEXT_CHARS),
            )] if cleaned else []

        if isinstance(content, list):
            events: list[HumanEvent] = []
            for block in content:
                events.extend(block_to_human_events(block))
            return events

        return []

    if cls == "UserMessage":
        content = get_attr_or_key(message, "content", [])

        if isinstance(content, str):
            cleaned = clean_text(content)
            if not cleaned:
                return []

            return [HumanEvent(
                kind="tool_result",
                title="tool",
                body=truncate(cleaned, MAX_TOOL_RESULT_CHARS),
                status="error" if cleaned.startswith("ERROR") else "ok",
                meta={
                    "tool": "tool_result",
                },
            )]

        if isinstance(content, list):
            events: list[HumanEvent] = []
            for block in content:
                events.extend(block_to_human_events(block))
            return events

        return []

    if cls == "ResultMessage":
        result = data.get("result") or ""
        body = result if result else compact_json({
            "subtype": data.get("subtype"),
            "duration_ms": data.get("duration_ms"),
            "num_turns": data.get("num_turns"),
            "total_cost_usd": data.get("total_cost_usd"),
            "is_error": data.get("is_error"),
        }, 4000)

        return [HumanEvent(
            kind="result",
            title="Resultado final",
            body=truncate(body, MAX_ASSISTANT_TEXT_CHARS),
            meta={
                "session_id": str(data.get("session_id", "") or ""),
            },
        )]

    return []


# =========================
# Claude SDK runner
# =========================

def claude_options_supports(field_name: str) -> bool:
    try:
        signature = inspect.signature(ClaudeAgentOptions)
    except Exception:
        return False

    return field_name in signature.parameters


def build_sdk_options(resume_session_id: str = "") -> ClaudeAgentOptions:
    env = os.environ.copy()
    env["HOME"] = resolve_runtime_home()
    env["PATH"] = build_runtime_path(env["HOME"])

    mcp_servers = {
        "leantime": {
            "type": "stdio",
            "command": LEANTIME_MCP_WRAPPER,
            "args": [],
        }
    }

    option_kwargs: dict[str, Any] = {
        "cwd": REPO_PATH,
        "cli_path": CLAUDE_BIN,
        "permission_mode": CLAUDE_PERMISSION_MODE,
        "max_turns": CLAUDE_MAX_TURNS,
        "max_budget_usd": CLAUDE_MAX_BUDGET_USD,
        "include_partial_messages": False,
        "mcp_servers": mcp_servers,
        "allowed_tools": [
            "Read",
            "Write",
            "Edit",
            "MultiEdit",
            "Bash",
            "Glob",
            "Grep",
            "LS",
            "TodoWrite",
            "Agent",
            "Task",
            "mcp__leantime__*",
        ],
        "disallowed_tools": [
            "Bash(git push:*)",
            "Bash(git merge:*)",
            "Bash(git rebase:*)",
            "Bash(git checkout master:*)",
            "Bash(git checkout main:*)",
            "Bash(sudo:*)",
            "Bash(rm -rf:*)",
        ],
        "env": env,
        "extra_args": {
            "agent": CLAUDE_AGENT,
        },
    }

    if resume_session_id and claude_options_supports("resume"):
        option_kwargs["resume"] = resume_session_id

    return ClaudeAgentOptions(**option_kwargs)


async def run_claude_sdk_for_task_async(
    task: dict[str, Any],
    trigger: str = "ready",
    review_comments: list[dict[str, Any]] | None = None,
    previous_session_id: str = "",
) -> tuple[bool, str]:
    ticket_id = int(task["id"])
    headline = task.get("headline", "")
    prompt = build_prompt(
        task,
        trigger=trigger,
        review_comments=review_comments,
        previous_session_id=previous_session_id,
    )

    logger = LiveReportLogger(ticket_id=ticket_id, headline=headline, prompt=prompt)
    can_resume = bool(previous_session_id and claude_options_supports("resume"))
    options = build_sdk_options(
        resume_session_id=previous_session_id if can_resume else "",
    )

    ok = True

    try:
        if previous_session_id:
            logger.event(HumanEvent(
                kind="system",
                title="Contexto de sesión",
                body=(
                    f"Session ID previo: {previous_session_id}\n"
                    f"Resume SDK activo: {'sí' if can_resume else 'no'}"
                ),
                meta={
                    "tool": "system",
                    "session_id": previous_session_id,
                },
            ))

        async def consume():
            async for message in query(prompt=prompt, options=options):
                logger.raw(message)

                if type(message).__name__ == "ResultMessage":
                    logger.set_result_stats(message)

                for event in sdk_message_to_events(message):
                    logger.event(event)

        await asyncio.wait_for(
            consume(),
            timeout=CLAUDE_TIMEOUT_SECONDS,
        )

    except asyncio.TimeoutError:
        ok = False
        logger.event(HumanEvent(
            kind="tool_result",
            title="worker-timeout",
            body="Claude SDK alcanzó el timeout total de ejecución.",
            status="error",
            meta={
                "tool": "worker",
            },
        ))

    except Exception as e:
        ok = False
        logger.event(HumanEvent(
            kind="tool_result",
            title="worker-error",
            body=repr(e),
            status="error",
            meta={
                "tool": "worker",
            },
        ))

    finally:
        logger.finish(ok)
        logger.close()

    return ok, logger.session_id


def run_claude_for_task(
    task: dict[str, Any],
    trigger: str = "ready",
    review_comments: list[dict[str, Any]] | None = None,
    previous_session_id: str = "",
) -> tuple[bool, str]:
    ticket_id = int(task["id"])
    headline = task.get("headline", "")

    print(
        f"[INFO] Launching Claude Agent SDK for ticket {ticket_id}: {headline} (trigger={trigger})",
        flush=True,
    )

    try:
        return asyncio.run(
            run_claude_sdk_for_task_async(
                task,
                trigger=trigger,
                review_comments=review_comments,
                previous_session_id=previous_session_id,
            )
        )
    except Exception as e:
        print(
            f"[ERROR] Claude SDK runner crashed for ticket {ticket_id}: {e}",
            flush=True,
        )
        return False, ""


def process_ready_task(task: dict[str, Any], state: dict[str, Any]) -> None:
    ticket_id = int(task["id"])
    headline = task.get("headline", "")
    processed = {
        int(task_id)
        for task_id in state.get("processed_ready_tasks", [])
    }

    if ticket_id in processed:
        return

    print(
        f"[INFO] Found Ready ticket {ticket_id}: {headline}",
        flush=True,
    )

    if not ensure_agent_users_provisioned():
        print(
            "[ERROR] Could not provision Leantime agent users. "
            f"Skipping ticket {ticket_id}.",
            flush=True,
        )
        return

    updated = update_ticket_status(ticket_id, IN_PROGRESS_STATUS_ID)

    if updated is None:
        print(
            f"[ERROR] Could not move ticket {ticket_id} to In Progress. Skipping.",
            flush=True,
        )
        return

    ok, session_id = run_claude_for_task(task, trigger="ready")

    processed.add(ticket_id)
    state["processed_ready_tasks"] = sorted(processed)

    latest_signature = latest_comment_signature(get_review_relevant_comments(ticket_id))
    if latest_signature:
        review_markers = state.setdefault("review_comment_markers", {})
        review_markers[str(ticket_id)] = latest_signature

    if session_id:
        ticket_sessions = state.setdefault("ticket_sessions", {})
        ticket_sessions[str(ticket_id)] = session_id
        sync_ticket_session_marker(ticket_id, session_id)

    save_worker_state(state)

    if not ok:
        print(
            f"[ERROR] Claude finished with error for ticket {ticket_id}. Review worker logs.",
            flush=True,
        )


def process_review_feedback_task(task: dict[str, Any], state: dict[str, Any]) -> None:
    ticket_id = int(task["id"])
    headline = task.get("headline", "")
    comments = get_review_relevant_comments(ticket_id)
    latest_signature = latest_comment_signature(comments)

    review_markers = state.setdefault("review_comment_markers", {})
    ticket_sessions = state.setdefault("ticket_sessions", {})
    known_signature = str(review_markers.get(str(ticket_id), "") or "")
    previous_session_id = (
        get_ticket_session_id_from_comments(ticket_id)
        or str(ticket_sessions.get(str(ticket_id), "") or "")
    )

    if not latest_signature:
        return

    if not known_signature:
        review_markers[str(ticket_id)] = latest_signature
        save_worker_state(state)
        return

    if latest_signature == known_signature:
        return

    pending_comments = comments_after_signature(comments, known_signature)

    print(
        f"[INFO] Found new Review feedback for ticket {ticket_id}: {headline}",
        flush=True,
    )

    if not ensure_agent_users_provisioned():
        print(
            "[ERROR] Could not provision Leantime agent users. "
            f"Skipping review feedback for ticket {ticket_id}.",
            flush=True,
        )
        return

    updated = update_ticket_status(ticket_id, IN_PROGRESS_STATUS_ID)
    if updated is None:
        print(
            f"[ERROR] Could not move ticket {ticket_id} back to In Progress.",
            flush=True,
        )
        return

    ok, session_id = run_claude_for_task(
        task,
        trigger="review_feedback",
        review_comments=pending_comments,
        previous_session_id=previous_session_id,
    )

    refreshed_comments = get_review_relevant_comments(ticket_id)
    refreshed_signature = latest_comment_signature(refreshed_comments)
    if refreshed_signature:
        review_markers[str(ticket_id)] = refreshed_signature

    if session_id:
        ticket_sessions[str(ticket_id)] = session_id
        sync_ticket_session_marker(ticket_id, session_id)

    save_worker_state(state)

    if not ok:
        print(
            f"[ERROR] Claude finished with error while processing review feedback for ticket {ticket_id}.",
            flush=True,
        )


# =========================
# Main loop
# =========================

def main():
    print("[INFO] PixelAgents Leantime Ready/Review worker started", flush=True)
    print(f"[INFO] Project ID: {PROJECT_ID}", flush=True)
    print(f"[INFO] Ready status ID: {READY_STATUS_ID}", flush=True)
    print(f"[INFO] In Progress status ID: {IN_PROGRESS_STATUS_ID}", flush=True)
    print(f"[INFO] Review status ID: {REVIEW_STATUS_ID}", flush=True)
    print(f"[INFO] Poll seconds: {POLL_SECONDS}", flush=True)
    print(f"[INFO] Repo path: {REPO_PATH}", flush=True)
    print(f"[INFO] Claude binary: {CLAUDE_BIN}", flush=True)
    print(f"[INFO] Permission mode: {CLAUDE_PERMISSION_MODE}", flush=True)
    print("[INFO] Using Claude Agent SDK + modular two-panel live HTML viewer", flush=True)

    state = load_worker_state()

    while True:
        ready_tasks = get_ready_tasks()
        review_tasks = get_review_tasks()

        for task in ready_tasks:
            process_ready_task(task, state)

        for task in review_tasks:
            process_review_feedback_task(task, state)

        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    main()
