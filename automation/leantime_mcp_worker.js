#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");
const { spawn } = require("child_process");

const LEANTIME_API_URL = process.env.LEANTIME_API_URL || "http://127.0.0.1:8090/api/jsonrpc";
const LEANTIME_API_KEY = process.env.LEANTIME_API_KEY || "";
const PROJECT_ID = Number(process.env.LEANTIME_PROJECT_ID || "3");
const READY_STATUS_ID = Number(process.env.READY_STATUS_ID || "1");
const IN_PROGRESS_STATUS_ID = Number(process.env.IN_PROGRESS_STATUS_ID || "2");
const REVIEW_STATUS_ID_ENV = process.env.REVIEW_STATUS_ID;
const CONFIGURED_REVIEW_STATUS_ID = Number(REVIEW_STATUS_ID_ENV || "3");
const REVIEW_STATUS_NAME = String(process.env.REVIEW_STATUS_NAME || "Review");
const POLL_SECONDS = Number(process.env.POLL_SECONDS || "30");
const REPO_PATH = process.env.PIXELAGENTS_REPO || "/home/debian/Proyectos/PixelAgents";
const LOG_DIR = process.env.WORKER_LOG_DIR || path.join(REPO_PATH, "automation", "logs");
const STATE_FILE = process.env.WORKER_STATE_FILE || path.join(resolveHome(), ".config", "pixelagents", "leantime-mcp-worker-state.json");
const CLAUDE_BIN = process.env.CLAUDE_BIN || "claude";
const CLAUDE_MODEL = process.env.CLAUDE_MODEL || "";
const CLAUDE_MAX_TURNS = Number(process.env.CLAUDE_MAX_TURNS || "80");
const CLAUDE_NOTE_PREFIX = "[CLAUDE_AUTOMATION]";
const CLAUDE_SESSION_MARKER_PREFIX = "[CLAUDE_SESSION]";
const CLAUDE_RESOLVED_MARKER_PREFIX = "[CLAUDE_RESOLVED]";
const CLAUDE_SUMMARY_MARKER_PREFIX = "[CLAUDE_SUMMARY]";
const CLAUDE_STARTED_MARKER_PREFIX = "[CLAUDE_STARTED]";
const CLAUDE_FINISHED_MARKER_PREFIX = "[CLAUDE_FINISHED]";
const AGENT_TAG_PREFIXES = ["session=", "branch=", "trigger="];
let resolvedReviewStatusId = CONFIGURED_REVIEW_STATUS_ID;

function resolveHome() {
  return process.env.HOME || "/home/debian";
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function ensureDir(dir) {
  fs.mkdirSync(dir, { recursive: true });
}

function nowIso() {
  return new Date().toISOString();
}

function escapeHtml(value) {
  return String(value == null ? "" : value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function formatTimestamp(ts) {
  const ms = Number(ts) * 1000;
  if (!Number.isFinite(ms)) {
    return "";
  }
  return new Date(ms).toISOString().replace("T", " ").replace(/\.\d+Z$/, "Z");
}

const REPORT_HTML_STYLE = `
:root { color-scheme: dark light; }
* { box-sizing: border-box; }
body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif; background: #0d1117; color: #c9d1d9; }
header { padding: 24px 32px; border-bottom: 1px solid #30363d; background: #161b22; position: sticky; top: 0; z-index: 1; }
header h1 { margin: 0 0 8px; font-size: 18px; font-weight: 600; }
header .meta { display: flex; flex-wrap: wrap; gap: 16px 24px; font-size: 13px; color: #8b949e; }
header .meta span strong { color: #c9d1d9; font-weight: 600; }
header .status { display: inline-block; padding: 2px 10px; border-radius: 999px; font-size: 12px; font-weight: 600; }
header .status.done { background: #1f6feb33; color: #79c0ff; }
header .status.error { background: #f8514933; color: #ff7b72; }
header .status.running { background: #d2992233; color: #e3b341; }
main { padding: 24px 32px 64px; max-width: 1200px; margin: 0 auto; }
details.event { background: #161b22; border: 1px solid #30363d; border-radius: 8px; margin-bottom: 12px; overflow: hidden; }
details.event[open] { border-color: #58a6ff66; }
details.event > summary { list-style: none; padding: 12px 16px; cursor: pointer; display: flex; gap: 12px; align-items: center; user-select: none; }
details.event > summary::-webkit-details-marker { display: none; }
details.event > summary::before { content: "▸"; color: #8b949e; font-size: 12px; transition: transform .15s; }
details.event[open] > summary::before { transform: rotate(90deg); }
details.event > summary .kind { padding: 1px 8px; border-radius: 4px; font-size: 11px; font-weight: 700; letter-spacing: .5px; text-transform: uppercase; background: #30363d; color: #c9d1d9; }
details.event[data-kind="result"] > summary .kind { background: #1f6feb33; color: #79c0ff; }
details.event[data-kind="error"] > summary .kind { background: #f8514933; color: #ff7b72; }
details.event[data-kind="system"] > summary .kind { background: #6e768133; color: #8b949e; }
details.event > summary .title { flex: 1; font-weight: 600; }
details.event > summary .ts { font-size: 12px; color: #8b949e; font-variant-numeric: tabular-nums; }
details.event > pre { margin: 0; padding: 12px 16px; border-top: 1px solid #30363d; background: #0d1117; overflow-x: auto; font-size: 12.5px; line-height: 1.5; white-space: pre-wrap; word-break: break-word; }
.toolbar { display: flex; gap: 8px; margin-bottom: 16px; }
.toolbar button { background: #21262d; color: #c9d1d9; border: 1px solid #30363d; border-radius: 6px; padding: 6px 12px; font-size: 12px; cursor: pointer; }
.toolbar button:hover { background: #30363d; }
@media (prefers-color-scheme: light) {
  body { background: #ffffff; color: #1f2328; }
  header { background: #f6f8fa; border-color: #d0d7de; }
  header .meta { color: #57606a; }
  header .meta span strong { color: #1f2328; }
  details.event { background: #f6f8fa; border-color: #d0d7de; }
  details.event > pre { background: #ffffff; border-color: #d0d7de; }
  .toolbar button { background: #ffffff; color: #1f2328; border-color: #d0d7de; }
  .toolbar button:hover { background: #f6f8fa; }
}
`;

const REPORT_HTML_SCRIPT = `
document.querySelectorAll(".js-expand-all").forEach((btn) => btn.addEventListener("click", () => {
  document.querySelectorAll("details.event").forEach((d) => { d.open = true; });
}));
document.querySelectorAll(".js-collapse-all").forEach((btn) => btn.addEventListener("click", () => {
  document.querySelectorAll("details.event").forEach((d) => { d.open = false; });
}));
`;

function renderReportHtml(payload) {
  const status = String(payload.status || "running");
  const stats = payload.stats || {};
  const events = Array.isArray(payload.events) ? payload.events : [];
  const cost = Number(stats.result_cost_usd || 0).toFixed(4);
  const turns = Number(stats.num_turns || 0);
  const elapsed = (Number(payload.updated_at || 0) - Number(payload.started_at || 0)).toFixed(1);

  const eventsHtml = events.map((event) => {
    const kind = String(event.kind || "");
    const title = escapeHtml(event.title || "");
    const body = escapeHtml(event.body || "");
    const ts = formatTimestamp(event.ts);
    const isOpen = (kind === "result" || kind === "error") ? " open" : "";
    return `<details class="event" data-kind="${escapeHtml(kind)}"${isOpen}>
  <summary><span class="kind">${escapeHtml(kind)}</span><span class="title">${title}</span><span class="ts">${escapeHtml(ts)}</span></summary>
  <pre>${body}</pre>
</details>`;
  }).join("\n");

  return `<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Ticket #${escapeHtml(payload.ticket_id)} · ${escapeHtml(payload.headline || "")}</title>
<style>${REPORT_HTML_STYLE}</style>
</head>
<body>
<header>
  <h1>Ticket #${escapeHtml(payload.ticket_id)} · ${escapeHtml(payload.headline || "")}</h1>
  <div class="meta">
    <span class="status ${escapeHtml(status)}">${escapeHtml(status)}</span>
    <span><strong>Eventos:</strong> ${events.length}</span>
    <span><strong>Turnos:</strong> ${turns}</span>
    <span><strong>Coste:</strong> $${escapeHtml(cost)}</span>
    <span><strong>Duración:</strong> ${escapeHtml(elapsed)}s</span>
    <span><strong>Iniciado:</strong> ${escapeHtml(formatTimestamp(payload.started_at))}</span>
    <span><strong>Actualizado:</strong> ${escapeHtml(formatTimestamp(payload.updated_at))}</span>
  </div>
</header>
<main>
  <div class="toolbar">
    <button type="button" class="js-expand-all">Expandir todo</button>
    <button type="button" class="js-collapse-all">Colapsar todo</button>
    <a class="toolbar" href="live.log" style="margin-left:auto;text-decoration:none;color:inherit;"><button type="button">Ver live.log</button></a>
  </div>
  ${eventsHtml || "<p style=\"color:#8b949e\">(sin eventos)</p>"}
</main>
<script>${REPORT_HTML_SCRIPT}</script>
</body>
</html>`;
}

function readJson(filePath, fallback) {
  try {
    return JSON.parse(fs.readFileSync(filePath, "utf8"));
  } catch {
    return fallback;
  }
}

function writeJson(filePath, value) {
  ensureDir(path.dirname(filePath));
  fs.writeFileSync(filePath, JSON.stringify(value, null, 2), "utf8");
}

function appendLine(filePath, line) {
  ensureDir(path.dirname(filePath));
  fs.appendFileSync(filePath, line + "\n", "utf8");
}

function loadState() {
  const raw = readJson(STATE_FILE, null);
  if (!raw || typeof raw !== "object") {
    return {
      processedReadyTasks: [],
      reviewCommentMarkers: {},
      ticketSessions: {},
    };
  }

  return {
    processedReadyTasks: Array.isArray(raw.processedReadyTasks) ? raw.processedReadyTasks : [],
    reviewCommentMarkers: raw.reviewCommentMarkers && typeof raw.reviewCommentMarkers === "object" ? raw.reviewCommentMarkers : {},
    ticketSessions: raw.ticketSessions && typeof raw.ticketSessions === "object" ? raw.ticketSessions : {},
  };
}

function saveState(state) {
  writeJson(STATE_FILE, state);
}

async function rpc(method, params = {}) {
  const response = await fetch(LEANTIME_API_URL, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "x-api-key": LEANTIME_API_KEY,
    },
    body: JSON.stringify({
      jsonrpc: "2.0",
      id: 1,
      method,
      params,
    }),
  });

  const text = await response.text();
  let payload;
  try {
    payload = JSON.parse(text);
  } catch {
    throw new Error(`Invalid JSON-RPC response for ${method}: ${text}`);
  }

  if (payload.error) {
    throw new Error(`RPC error for ${method}: ${JSON.stringify(payload.error)}`);
  }

  return payload.result;
}

async function getAllTasks() {
  const result = await rpc("leantime.rpc.tickets.getAll", {});
  return Array.isArray(result) ? result : [];
}

async function getTicket(ticketId) {
  const result = await rpc("leantime.rpc.tickets.getTicket", { id: Number(ticketId) });
  return result && typeof result === "object" ? result : null;
}

async function getTasksByStatus(statusIds) {
  const ids = new Set(statusIds.map(Number));
  const tasks = await getAllTasks();
  return tasks.filter((task) => {
    const projectId = Number(task.projectId || 0);
    const statusId = Number(task.status || -1);
    return projectId === PROJECT_ID && ids.has(statusId);
  });
}

async function getProjectStatuses() {
  const attempts = [
    ["leantime.rpc.tickets.getStatusLabels", {}],
    ["leantime.rpc.tickets.getStatusLabels", { projectId: PROJECT_ID }],
    ["leantime.rpc.projects.getProjectStatuses", { projectId: PROJECT_ID }],
  ];

  for (const [method, params] of attempts) {
    try {
      const result = await rpc(method, params);
      if (result && typeof result === "object") {
        return result;
      }
    } catch (error) {
      logGlobal(`[WARN] Could not read project statuses using ${method}: ${String(error.message || error)}`);
    }
  }

  return {};
}

function normalizeStatusEntries(rawStatuses) {
  const entries = [];

  if (Array.isArray(rawStatuses)) {
    for (const item of rawStatuses) {
      if (!item || typeof item !== "object") {
        continue;
      }
      const id = Number(item.id || item.status || item.key || 0);
      const name = String(item.name || item.label || item.statusLabel || item.value || "");
      if (id && name) {
        entries.push({ id, name });
      }
    }
    return entries;
  }

  if (rawStatuses && typeof rawStatuses === "object") {
    for (const [key, value] of Object.entries(rawStatuses)) {
      const id = Number(key);
      if (!id || !value || typeof value !== "object") {
        continue;
      }
      const name = String(value.name || value.label || value.statusLabel || "");
      if (name) {
        entries.push({ id, name });
      }
    }
  }

  return entries;
}

function formatStatusEntries(entries) {
  if (!entries.length) {
    return "(sin datos)";
  }

  return entries
    .map((entry) => `${entry.id}:${entry.name}`)
    .join(", ");
}

async function resolveReviewStatusId() {
  const rawStatuses = await getProjectStatuses();
  const entries = normalizeStatusEntries(rawStatuses);
  if (!entries.length) {
    logGlobal(`[WARN] Could not resolve Leantime status catalog. Using configured REVIEW_STATUS_ID=${CONFIGURED_REVIEW_STATUS_ID}.`);
    return CONFIGURED_REVIEW_STATUS_ID;
  }

  const wanted = REVIEW_STATUS_NAME.trim().toLowerCase();
  const exact = entries.find((entry) => entry.name.trim().toLowerCase() === wanted);
  if (exact) {
    if (Number.isFinite(CONFIGURED_REVIEW_STATUS_ID) && exact.id !== CONFIGURED_REVIEW_STATUS_ID) {
      logGlobal(`[WARN] REVIEW_STATUS_ID=${CONFIGURED_REVIEW_STATUS_ID} does not match REVIEW_STATUS_NAME=${JSON.stringify(REVIEW_STATUS_NAME)}. Using exact match id=${exact.id}. Catalog: ${formatStatusEntries(entries)}`);
    }
    return exact.id;
  }

  logGlobal(`[WARN] REVIEW_STATUS_NAME=${JSON.stringify(REVIEW_STATUS_NAME)} not found in Leantime status catalog. Falling back to REVIEW_STATUS_ID=${CONFIGURED_REVIEW_STATUS_ID}. Catalog: ${formatStatusEntries(entries)}`);
  return CONFIGURED_REVIEW_STATUS_ID;
}

async function updateTicketStatus(ticketId, statusId) {
  const ticket = await getTicket(ticketId);
  if (!ticket) {
    throw new Error(`Could not read ticket ${ticketId} before updating status.`);
  }

  const projectId = Number(ticket.projectId || PROJECT_ID);
  const attempts = [
    { values: { id: Number(ticketId), status: Number(statusId), projectId } },
    { id: Number(ticketId), values: { status: Number(statusId), projectId } },
  ];

  for (const params of attempts) {
    try {
      return await rpc("leantime.rpc.tickets.updateTicket", params);
    } catch (error) {
      logGlobal(`[WARN] updateTicket failed for ticket ${ticketId}: ${String(error.message || error)}`);
    }
  }

  throw new Error(`Could not move ticket ${ticketId} to status ${statusId}.`);
}

async function updateTicketFields(ticketId, values, ticket = null) {
  const existingTicket = ticket || await getTicket(ticketId);
  if (!existingTicket) {
    throw new Error(`Could not read ticket ${ticketId} before updating fields.`);
  }

  const projectId = Number(existingTicket.projectId || PROJECT_ID);
  const normalizedValues = {
    ...values,
    id: Number(ticketId),
    projectId,
  };

  const attempts = [
    { values: normalizedValues },
    { id: Number(ticketId), values: normalizedValues },
    normalizedValues,
  ];

  for (const params of attempts) {
    try {
      return await rpc("leantime.rpc.tickets.updateTicket", params);
    } catch (error) {
      logGlobal(`[WARN] updateTicketFields failed for ticket ${ticketId}: ${String(error.message || error)}`);
    }
  }

  throw new Error(`Could not update ticket ${ticketId}.`);
}

async function addTicketComment(ticketId, comment, commentParent = "") {
  const father = String(commentParent || "0");
  const params = {
    values: { text: String(comment), father },
    module: "ticket",
    entityId: Number(ticketId),
    entity: {},
  };

  let result;
  try {
    result = await rpc("leantime.rpc.comments.addComment", params);
  } catch (error) {
    throw new Error(`Could not add discussion comment for ticket ${ticketId}: ${String(error && error.message || error)}`);
  }

  if (result === false || result === null || result === undefined) {
    throw new Error(`Could not add discussion comment for ticket ${ticketId}: addComment returned ${JSON.stringify(result)}`);
  }
  return result;
}

async function getTicketComments(ticketId) {
  const attempts = [
    ["leantime.rpc.comments.getComments", { module: "ticket", entityId: Number(ticketId) }],
    ["leantime.rpc.comments.getComments", { module: "tickets", entityId: Number(ticketId) }],
    ["leantime.rpc.comments.getComments", { module: "ticket", entityId: String(ticketId) }],
    ["leantime.rpc.comments.getComments", { module: "tickets", entityId: String(ticketId) }],
    ["leantime.rpc.comments.getComments", { moduleId: Number(ticketId), commentModule: "tickets" }],
    ["leantime.rpc.comments.getComments", { moduleId: String(ticketId), commentModule: "tickets" }],
  ];

  const errors = [];

  for (const [method, params] of attempts) {
    try {
      const result = await rpc(method, params);
      if (Array.isArray(result)) {
        return result.filter((item) => item && typeof item === "object");
      }
      if (result && typeof result === "object") {
        for (const key of ["comments", "result", "data", "rows"]) {
          if (Array.isArray(result[key])) {
            return result[key].filter((item) => item && typeof item === "object");
          }
        }
      }
    } catch (error) {
      errors.push(`${method} ${JSON.stringify(params)} -> ${String(error.message || error)}`);
    }
  }

  if (errors.length) {
    logGlobal(`[WARN] Could not read comments for ticket ${ticketId}. Attempts:\n- ${errors.join("\n- ")}`);
  }

  return [];
}

function getCommentBody(comment) {
  return String(comment.text || comment.comment || comment.content || comment.description || "").trim();
}

function getCommentId(comment) {
  return String(comment.id || comment.commentId || comment.pk || "");
}

function isAutomationComment(comment) {
  return getCommentBody(comment).startsWith(CLAUDE_NOTE_PREFIX);
}

function isSessionMarkerComment(comment) {
  return getCommentBody(comment).startsWith(CLAUDE_SESSION_MARKER_PREFIX);
}

function isResolvedMarkerComment(comment) {
  return getCommentBody(comment).startsWith(CLAUDE_RESOLVED_MARKER_PREFIX);
}

function isSummaryMarkerComment(comment) {
  return getCommentBody(comment).startsWith(CLAUDE_SUMMARY_MARKER_PREFIX);
}

function isLifecycleMarkerComment(comment) {
  const body = getCommentBody(comment);
  return body.startsWith(CLAUDE_STARTED_MARKER_PREFIX) || body.startsWith(CLAUDE_FINISHED_MARKER_PREFIX);
}

function extractSessionIdFromMarkerComment(comment) {
  const match = getCommentBody(comment).match(/session_id=([A-Za-z0-9._:-]+)/);
  return match ? match[1] : "";
}

function extractResolvedCommentId(comment) {
  const match = getCommentBody(comment).match(/comment_id=([A-Za-z0-9._:-]+)/);
  return match ? match[1] : "";
}

function getCommentTimestamp(comment) {
  return String(
    comment.userModified
    || comment.rawDate
    || comment.date
    || comment.dateModified
    || comment.dateCreated
    || ""
  );
}

function sortComments(comments) {
  return [...comments].sort((a, b) => {
    const aId = Number(getCommentId(a) || 0);
    const bId = Number(getCommentId(b) || 0);
    if (aId !== bId) {
      return aId - bId;
    }
    return getCommentTimestamp(a).localeCompare(getCommentTimestamp(b));
  });
}

function getReviewRelevantComments(comments) {
  return comments.filter((comment) =>
    !isAutomationComment(comment)
    && !isSessionMarkerComment(comment)
    && !isResolvedMarkerComment(comment)
    && !isSummaryMarkerComment(comment)
    && !isLifecycleMarkerComment(comment)
  );
}

function commentSignature(comment) {
  const id = getCommentId(comment);
  const timestamp = getCommentTimestamp(comment);
  const preview = getCommentBody(comment).replace(/\s+/g, " ").slice(0, 140);
  const parent = String(comment.commentParent || "");
  return `${id}|${timestamp}|${parent}|${preview}`;
}

function getTicketSessionIdFromComments(comments) {
  const markerComments = comments.filter((comment) => isSessionMarkerComment(comment));
  if (!markerComments.length) {
    return "";
  }

  const latestMarker = sortComments(markerComments)[markerComments.length - 1];
  return extractSessionIdFromMarkerComment(latestMarker);
}

async function syncTicketSessionMarker(ticketId, sessionId) {
  if (!sessionId) {
    return;
  }

  const comments = await getTicketComments(ticketId);
  if (getTicketSessionIdFromComments(comments) === sessionId) {
    return;
  }

  await addTicketComment(
    ticketId,
    `${CLAUDE_SESSION_MARKER_PREFIX} session_id=${sessionId}\nEtiqueta tecnica para reanudar o reconstruir el contexto de Claude.`,
  );
}

async function syncTicketSummaryComment(ticketId, finalResult, sessionId = "") {
  const summary = String(finalResult || "").trim();
  if (!summary) {
    return;
  }

  const comments = await getTicketComments(ticketId);
  for (const comment of comments) {
    const body = getCommentBody(comment);
    if (body.startsWith(CLAUDE_SUMMARY_MARKER_PREFIX) && body.includes(summary)) {
      return;
    }
  }

  const marker = `${CLAUDE_SUMMARY_MARKER_PREFIX}${sessionId ? ` session_id=${sessionId}` : ""}\n${summary}`;
  await addTicketComment(ticketId, marker);
}

async function restoreTicketDescriptionIfChanged(ticketId, originalDescription) {
  const currentTicket = await getTicket(ticketId);
  if (!currentTicket) {
    return;
  }

  const currentDescription = String(currentTicket.description || "");
  const expectedDescription = String(originalDescription || "");
  if (currentDescription === expectedDescription) {
    return;
  }

  logGlobal(`[WARN] Restoring immutable description for ticket ${ticketId}.`);
  await updateTicketFields(ticketId, { description: expectedDescription }, currentTicket);
}

function hasResolutionReply(comment, allComments) {
  const targetCommentId = getCommentId(comment);
  if (!targetCommentId) {
    return false;
  }

  for (const candidate of allComments) {
    if (!isResolvedMarkerComment(candidate)) {
      continue;
    }

    const candidateParent = String(candidate.commentParent || "");
    const resolvedCommentId = extractResolvedCommentId(candidate);
    if (candidateParent === targetCommentId || resolvedCommentId === targetCommentId) {
      return true;
    }
  }

  return false;
}

async function acknowledgeResolvedReviewComments(ticketId, resolvedComments) {
  if (!resolvedComments.length) {
    return;
  }

  const allComments = await getTicketComments(ticketId);
  for (const comment of resolvedComments) {
    const commentId = getCommentId(comment);
    if (!commentId || hasResolutionReply(comment, allComments)) {
      continue;
    }

    await addTicketComment(
      ticketId,
      `${CLAUDE_RESOLVED_MARKER_PREFIX} comment_id=${commentId}\nCorregido y devuelto a Review. Si ves algo mas en este punto, anade otro comentario en este hilo.`,
      commentId,
    );
  }
}

function latestCommentSignature(comments) {
  if (!comments.length) {
    return "";
  }
  return commentSignature(sortComments(comments)[comments.length - 1]);
}

function commentsAfterSignature(comments, signature) {
  const ordered = sortComments(comments);
  if (!signature) {
    return ordered;
  }

  const index = ordered.findIndex((item) => commentSignature(item) === signature);
  if (index === -1) {
    return ordered;
  }
  return ordered.slice(index + 1);
}

function getCommentAuthor(comment) {
  const composed = `${String(comment.firstname || "").trim()} ${String(comment.lastname || "").trim()}`.trim();
  if (composed) {
    return composed;
  }
  return String(comment.fullName || comment.name || comment.user || comment.author || "desconocido");
}

function commentText(comment) {
  const id = getCommentId(comment) || "desconocido";
  const author = getCommentAuthor(comment);
  const body = getCommentBody(comment) || "(sin texto)";
  return `ID comentario: ${id}\nAutor: ${author}\n${body}`;
}

function buildPrompt(task, trigger, reviewComments, previousSessionId) {
  const ticketId = Number(task.id);
  const headline = String(task.headline || "");
  const description = String(task.description || "");

  const triggerContext = trigger === "review_feedback"
    ? [
        "- Esta tarea ya estaba en Review.",
        "- El worker ha detectado comentarios nuevos del humano en la sección Discusión.",
        "- Debes tratar esos comentarios como feedback correctivo y reanudar el trabajo.",
      ]
    : [
        "- Esta tarea fue movida a Ready por un humano.",
        "- El worker la ha detectado y debe comenzar el flujo de trabajo.",
      ];

  const reviewBlock = reviewComments.length
    ? `\nComentarios nuevos a procesar:\n${reviewComments.map((comment, index) => `Comentario ${index + 1}\n${commentText(comment)}`).join("\n\n")}`
    : "";

  const sessionBlock = previousSessionId
    ? `\nContexto de conversación Claude previo:\n- Session ID anterior: ${previousSessionId}\n- Reanuda el trabajo usando este mismo contexto si es posible.`
    : "";

  return [
    "Use the coordinator subagent for this task.",
    `Trabaja sobre la tarea Leantime ID ${ticketId} del proyecto PixelAgents.`,
    "",
    "Título:",
    headline,
    "",
    "Descripción original de la tarea:",
    description,
    "",
    "Contexto operativo:",
    ...triggerContext,
    "- Usa exclusivamente las herramientas del MCP oficial de Leantime para leer y modificar tareas, subtareas y comentarios.",
    "- No uses JSON-RPC directo ni `./tools/leantime.sh` para mutaciones funcionales de Leantime.",
    "- La descripción original de la tarea es inmutable: no la edites, no la sustituyas y no escribas resúmenes ahí.",
    "- Todo resumen de progreso, rama, commit, validación o corrección debe ir en Discusión.",
    `- Todos los comentarios automáticos que escribas en Discusión deben empezar por ${CLAUDE_NOTE_PREFIX}.`,
    "- NO modifiques el status del ticket bajo ningún concepto. El worker controla el status: lo deja en Review automáticamente al terminar correctamente, y en In Progress si Claude falla. No invoques tools para cambiar status, ni te apoyes en el estado actual del ticket: trabaja siempre como si estuviera en In Progress.",
    "- NO modifiques las etiquetas (tags) del ticket. El worker añade automáticamente session=<id> y branch=<rama> al terminar.",
    reviewBlock,
    sessionBlock,
    "",
    "Objetivos obligatorios:",
    "- Si el trabajo no es trivial, crea subtareas reales en Leantime.",
    "- Mantén el estado real del trabajo sincronizado con Leantime mediante comentarios en Discusión (no por status ni por tags).",
    "- Si la corrección viene de un comentario concreto, responde en ese hilo y marca claramente que ya quedó resuelto.",
    "- No marques Done. No hagas merge a develop ni master.",
    "",
    "Resumen final (obligatorio en tu resultado):",
    "Devuelve un resumen MUY breve (3-6 líneas) que incluya, en líneas separadas y con esos prefijos exactos para que el worker pueda parsearlos:",
    "  Rama: `<nombre-de-rama>`",
    "  Último commit: `<sha-corto>` <mensaje>",
    "  Resumen: <una o dos frases con lo hecho>",
    "No repitas la rama, el commit ni el session_id en otros formatos: el worker los pondrá en tags. No incluyas listas largas de archivos ni explicaciones extensas.",
  ].join("\n").trim();
}

function logGlobal(message) {
  console.log(message);
}

class TicketLogger {
  constructor(ticketId, headline) {
    this.ticketId = Number(ticketId);
    this.headline = headline;
    this.ticketDir = path.join(LOG_DIR, `ticket-${ticketId}`);
    ensureDir(this.ticketDir);
    this.liveLogPath = path.join(this.ticketDir, "live.log");
    this.eventsPath = path.join(this.ticketDir, "events.json");
    this.reportPath = path.join(this.ticketDir, "report.md");
    this.reportHtmlPath = path.join(this.ticketDir, "report.html");
    this.startedAt = Date.now();
    this.events = [];
    this.stats = {
      assistant_messages: 0,
      tool_calls: 0,
      tool_results: 0,
      errors: 0,
      hidden_thinking_blocks: 0,
      result_cost_usd: 0,
      num_turns: 0,
      final_result: "",
    };
    this.status = "running";
    appendLine(this.liveLogPath, `${"=".repeat(80)}\nTicket #${ticketId} · ${headline}\nStarted: ${nowIso()}\n${"=".repeat(80)}`);
    this.flush();
  }

  log(kind, title, body) {
    const entry = {
      kind,
      title,
      body,
      ts: Date.now() / 1000,
    };
    this.events.push(entry);
    appendLine(this.liveLogPath, `\n[${kind.toUpperCase()}] ${title}\n${"-".repeat(80)}\n${body}\n${"-".repeat(80)}`);
    this.flush();
  }

  setResult(result) {
    this.stats.final_result = String(result.result || "");
    this.stats.result_cost_usd = Number(result.total_cost_usd || 0);
    this.stats.num_turns = Number(result.num_turns || 0);
    this.status = result.is_error ? "error" : "done";
    this.log("result", "Resultado final", this.stats.final_result || JSON.stringify(result, null, 2));
  }

  flush() {
    const payload = {
      ticket_id: this.ticketId,
      headline: this.headline,
      status: this.status,
      started_at: this.startedAt / 1000,
      updated_at: Date.now() / 1000,
      stats: this.stats,
      events: this.events,
    };
    writeJson(this.eventsPath, payload);

    const lines = [
      `# Ticket #${this.ticketId} · ${this.headline}`,
      "",
      `Estado: ${this.status}`,
      "",
      "## Eventos",
      "",
      ...this.events.flatMap((event) => [
        `### ${event.kind} · ${event.title}`,
        "",
        "```text",
        event.body,
        "```",
        "",
      ]),
    ];
    fs.writeFileSync(this.reportPath, lines.join("\n"), "utf8");

    try {
      fs.writeFileSync(this.reportHtmlPath, renderReportHtml(payload), "utf8");
    } catch (error) {
      logGlobal(`[WARN] Could not render report.html for ticket ${this.ticketId}: ${String(error && error.message || error)}`);
    }
  }
}

async function addStartedMarker(ticketId, trigger, previousSessionId) {
  const lines = [
    `${CLAUDE_STARTED_MARKER_PREFIX} ts=${nowIso()} trigger=${trigger}${previousSessionId ? ` resumes_session=${previousSessionId}` : ""}`,
    "Marca tecnica: instante en que el worker lanza Claude para esta tarea.",
  ];
  await addTicketComment(ticketId, lines.join("\n"));
}

async function addFinishedMarker(ticketId, claudeResult, startedAtMs, sessionId) {
  const elapsedSeconds = ((Date.now() - startedAtMs) / 1000).toFixed(1);
  const status = claudeResult && claudeResult.is_error ? "error" : "done";
  const turns = Number((claudeResult || {}).num_turns || 0);
  const cost = Number((claudeResult || {}).total_cost_usd || 0).toFixed(4);
  const lines = [
    `${CLAUDE_FINISHED_MARKER_PREFIX} ts=${nowIso()} duration=${elapsedSeconds}s status=${status} turns=${turns} cost=$${cost}${sessionId ? ` session_id=${sessionId}` : ""}`,
    "Marca tecnica: instante en que Claude termina y los datos de la ejecucion.",
  ];
  await addTicketComment(ticketId, lines.join("\n"));
}

function parseBranchFromResult(text) {
  const body = String(text || "");
  const patterns = [
    /(?:^|\n)[\s*_+\-]*Rama[\s*_]*:[\s*_]*[`'"]?([^`'"\n*]+?)[`'"]?[\s*_]*$/m,
    /\bbranch[:=]\s*[`'"]?([^`'"\s]+)/i,
    /git\s+push\s+(?:-u\s+)?\S+\s+([\w./-]+)/i,
  ];
  for (const re of patterns) {
    const match = body.match(re);
    if (match && match[1]) {
      const candidate = match[1].trim();
      if (/^(feature|fix|hotfix|bugfix|chore|docs|claude)\//.test(candidate)) {
        return candidate;
      }
    }
  }
  return "";
}

function existingTagsArray(ticket) {
  if (!ticket) {
    return [];
  }
  const raw = ticket.tags;
  if (Array.isArray(raw)) {
    return raw.map((tag) => String(tag).trim()).filter(Boolean);
  }
  if (raw == null || raw === "") {
    return [];
  }
  return String(raw).split(",").map((tag) => tag.trim()).filter(Boolean);
}

async function setTicketAgentTags(ticketId, { sessionId = "", branch = "", trigger = "" } = {}) {
  const ticket = await getTicket(ticketId);
  if (!ticket) {
    throw new Error(`Could not read ticket ${ticketId} before updating tags.`);
  }

  const previousTags = existingTagsArray(ticket);
  const preserved = previousTags.filter((tag) => !AGENT_TAG_PREFIXES.some((prefix) => tag.startsWith(prefix)));

  const agentTags = [];
  if (sessionId) {
    agentTags.push(`session=${sessionId}`);
  }
  if (branch) {
    agentTags.push(`branch=${branch}`);
  }
  if (trigger) {
    agentTags.push(`trigger=${trigger}`);
  }

  const merged = [...preserved, ...agentTags];
  const tagsString = merged.join(", ");

  if (tagsString === previousTags.join(", ")) {
    return;
  }

  await updateTicketFields(ticketId, { tags: tagsString }, ticket);
}

async function forceTicketStatus(ticketId, statusId, label) {
  const ticket = await getTicket(ticketId);
  const current = ticket ? Number(ticket.status || -1) : -1;
  if (current === Number(statusId)) {
    return;
  }
  logGlobal(`[INFO] Forcing ticket ${ticketId} status: ${current} -> ${statusId} (${label}).`);
  await updateTicketStatus(ticketId, statusId);
}

function runClaude(prompt, previousSessionId, logger) {
  return new Promise((resolve, reject) => {
    const args = ["-p", prompt, "--output-format", "json", "--max-turns", String(CLAUDE_MAX_TURNS), "--dangerously-skip-permissions"];
    if (CLAUDE_MODEL) {
      args.push("--model", CLAUDE_MODEL);
    }
    if (previousSessionId) {
      args.unshift(previousSessionId);
      args.unshift("--resume");
    }

    const ticketId = logger.ticketId;
    const startedAt = Date.now();
    logGlobal(`[INFO] Claude started ticket=${ticketId} resume=${previousSessionId ? "yes" : "no"} prompt_chars=${prompt.length} model=${CLAUDE_MODEL || "default"}`);
    logger.log("system", "Claude command", `${CLAUDE_BIN} ${args.join(" ")}`);

    const child = spawn(CLAUDE_BIN, args, {
      cwd: REPO_PATH,
      env: process.env,
      stdio: ["ignore", "pipe", "pipe"],
    });

    let stdout = "";
    let stderr = "";

    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString();
    });
    child.stderr.on("data", (chunk) => {
      const text = chunk.toString();
      stderr += text;
      appendLine(logger.liveLogPath, text.trimEnd());
    });

    child.on("error", (error) => {
      const elapsed = ((Date.now() - startedAt) / 1000).toFixed(1);
      logGlobal(`[ERROR] Claude spawn error ticket=${ticketId} elapsed=${elapsed}s: ${String(error.message || error)}`);
      reject(error);
    });
    child.on("close", (code) => {
      const elapsed = ((Date.now() - startedAt) / 1000).toFixed(1);
      if (stderr.trim()) {
        logger.log("system", "Claude stderr", stderr.trim());
      }

      if (code !== 0) {
        logGlobal(`[ERROR] Claude exited code=${code} ticket=${ticketId} elapsed=${elapsed}s`);
        return reject(new Error(`claude exited with code ${code}\n${stderr}`));
      }

      try {
        const parsed = JSON.parse(stdout);
        const cost = Number(parsed.total_cost_usd || 0);
        const turns = Number(parsed.num_turns || 0);
        const session = parsed.session_id || "none";
        const status = parsed.is_error ? "error" : "done";
        logGlobal(`[INFO] Claude finished ticket=${ticketId} status=${status} turns=${turns} cost=$${cost.toFixed(4)} elapsed=${elapsed}s session=${session}`);
        return resolve(parsed);
      } catch (error) {
        logGlobal(`[ERROR] Claude JSON parse failed ticket=${ticketId} elapsed=${elapsed}s: ${String(error.message || error)}`);
        return reject(new Error(`Could not parse Claude JSON output: ${error.message}\n${stdout}`));
      }
    });
  });
}

async function tryStep(logger, ticketId, label, fn) {
  try {
    await fn();
  } catch (error) {
    const message = String(error && error.message || error);
    if (logger) {
      logger.log("error", label, message);
    }
    logGlobal(`[WARN] ${label} ticket=${ticketId}: ${message}`);
  }
}

async function processReadyTask(task, state) {
  const ticketId = Number(task.id);
  const originalDescription = String(task.description || "");
  if (state.processedReadyTasks.includes(ticketId)) {
    return;
  }

  logGlobal(`[INFO] Found Ready ticket ${ticketId}: ${task.headline || ""}`);
  await updateTicketStatus(ticketId, IN_PROGRESS_STATUS_ID);

  const logger = new TicketLogger(ticketId, String(task.headline || "(sin titulo)"));
  const startedAtMs = Date.now();
  const previousSessionId = getTicketSessionIdFromComments(await getTicketComments(ticketId)) || state.ticketSessions[String(ticketId)] || "";

  await tryStep(logger, ticketId, "addStartedMarker", () => addStartedMarker(ticketId, "ready", previousSessionId));

  let result;
  let claudeOk = false;
  try {
    result = await runClaude(buildPrompt(task, "ready", [], previousSessionId), previousSessionId, logger);
    logger.setResult(result);
    claudeOk = !result.is_error;

    state.processedReadyTasks = Array.from(new Set([...state.processedReadyTasks, ticketId])).sort((a, b) => a - b);
    if (result.session_id) {
      state.ticketSessions[String(ticketId)] = result.session_id;
      await tryStep(logger, ticketId, "syncTicketSessionMarker", () => syncTicketSessionMarker(ticketId, result.session_id));
    }
  } catch (error) {
    logger.log("error", "Claude execution failed", String(error.message || error));
    logGlobal(`[ERROR] processReadyTask ticket=${ticketId}: ${String(error.message || error)}`);
  }

  const sessionId = (result && result.session_id) || previousSessionId || "";

  await tryStep(logger, ticketId, "addFinishedMarker", () => addFinishedMarker(ticketId, result || {}, startedAtMs, sessionId));

  await tryStep(logger, ticketId, "refreshReviewSignature", async () => {
    const latestSignature = latestCommentSignature(getReviewRelevantComments(await getTicketComments(ticketId)));
    if (latestSignature) {
      state.reviewCommentMarkers[String(ticketId)] = latestSignature;
    }
  });

  await tryStep(logger, ticketId, "restoreTicketDescriptionIfChanged", () => restoreTicketDescriptionIfChanged(ticketId, originalDescription));

  if (claudeOk && result) {
    await tryStep(logger, ticketId, "syncTicketSummaryComment", () => syncTicketSummaryComment(ticketId, result.result || "", sessionId));
    const branch = parseBranchFromResult(result.result);
    await tryStep(logger, ticketId, "setTicketAgentTags", () => setTicketAgentTags(ticketId, { sessionId, branch, trigger: "ready" }));
    await tryStep(logger, ticketId, "ensureReviewStatus", () => forceTicketStatus(ticketId, resolvedReviewStatusId, "ready->Review"));
  } else {
    await tryStep(logger, ticketId, "ensureInProgressOnError", () => forceTicketStatus(ticketId, IN_PROGRESS_STATUS_ID, "claude-error->In Progress"));
  }

  saveState(state);
}

async function processReviewTask(task, state) {
  const ticketId = Number(task.id);
  const originalDescription = String(task.description || "");
  const allComments = await getTicketComments(ticketId);
  const comments = getReviewRelevantComments(allComments);
  const latestSignature = latestCommentSignature(comments);
  const knownSignature = state.reviewCommentMarkers[String(ticketId)] || "";
  const previousSessionId = getTicketSessionIdFromComments(allComments) || state.ticketSessions[String(ticketId)] || "";
  const workerOwned = state.processedReadyTasks.includes(ticketId);

  logGlobal(`[INFO] Review scan ticket=${ticketId} comments=${comments.length} known_signature=${knownSignature ? "yes" : "no"} latest_signature=${latestSignature ? "yes" : "no"} previous_session=${previousSessionId ? "yes" : "no"} worker_owned=${workerOwned ? "yes" : "no"} title=${task.headline || ""}`);

  if (!latestSignature) {
    return;
  }

  let pendingComments;
  if (!knownSignature) {
    if (!workerOwned) {
      state.reviewCommentMarkers[String(ticketId)] = latestSignature;
      saveState(state);
      return;
    }
    pendingComments = sortComments(comments);
  } else if (latestSignature === knownSignature) {
    return;
  } else {
    pendingComments = commentsAfterSignature(comments, knownSignature);
  }

  logGlobal(`[INFO] Found new Review feedback for ticket ${ticketId}: ${task.headline || ""}`);

  await updateTicketStatus(ticketId, IN_PROGRESS_STATUS_ID);

  const logger = new TicketLogger(ticketId, String(task.headline || "(sin titulo)"));
  const startedAtMs = Date.now();

  await tryStep(logger, ticketId, "addStartedMarker", () => addStartedMarker(ticketId, "review_feedback", previousSessionId));

  let result;
  let claudeOk = false;
  try {
    result = await runClaude(buildPrompt(task, "review_feedback", pendingComments, previousSessionId), previousSessionId, logger);
    logger.setResult(result);
    claudeOk = !result.is_error;

    if (result.session_id) {
      state.ticketSessions[String(ticketId)] = result.session_id;
      await tryStep(logger, ticketId, "syncTicketSessionMarker", () => syncTicketSessionMarker(ticketId, result.session_id));
    }
  } catch (error) {
    logger.log("error", "Claude execution failed", String(error.message || error));
    logGlobal(`[ERROR] processReviewTask ticket=${ticketId}: ${String(error.message || error)}`);
  }

  const sessionId = (result && result.session_id) || previousSessionId || "";

  await tryStep(logger, ticketId, "addFinishedMarker", () => addFinishedMarker(ticketId, result || {}, startedAtMs, sessionId));

  await tryStep(logger, ticketId, "restoreTicketDescriptionIfChanged", () => restoreTicketDescriptionIfChanged(ticketId, originalDescription));

  if (claudeOk && result) {
    await tryStep(logger, ticketId, "syncTicketSummaryComment", () => syncTicketSummaryComment(ticketId, result.result || "", sessionId));
    await tryStep(logger, ticketId, "acknowledgeResolvedReviewComments", () => acknowledgeResolvedReviewComments(ticketId, pendingComments));
    const branch = parseBranchFromResult(result.result);
    await tryStep(logger, ticketId, "setTicketAgentTags", () => setTicketAgentTags(ticketId, { sessionId, branch, trigger: "review_feedback" }));
  }

  await tryStep(logger, ticketId, "refreshReviewSignature", async () => {
    const refreshedComments = getReviewRelevantComments(await getTicketComments(ticketId));
    const refreshedSignature = latestCommentSignature(refreshedComments);
    if (refreshedSignature) {
      state.reviewCommentMarkers[String(ticketId)] = refreshedSignature;
    }
  });

  if (claudeOk) {
    await tryStep(logger, ticketId, "ensureReviewStatus", () => forceTicketStatus(ticketId, resolvedReviewStatusId, "review-feedback->Review"));
  } else {
    await tryStep(logger, ticketId, "ensureInProgressOnError", () => forceTicketStatus(ticketId, IN_PROGRESS_STATUS_ID, "claude-error->In Progress"));
  }

  saveState(state);
}

async function main() {
  if (!LEANTIME_API_KEY) {
    throw new Error("Missing LEANTIME_API_KEY");
  }

  ensureDir(LOG_DIR);
  const state = loadState();
  resolvedReviewStatusId = await resolveReviewStatusId();

  logGlobal("[INFO] PixelAgents Leantime MCP worker started");
  logGlobal(`[INFO] Project ID: ${PROJECT_ID}`);
  logGlobal(`[INFO] Ready status ID: ${READY_STATUS_ID}`);
  logGlobal(`[INFO] In Progress status ID: ${IN_PROGRESS_STATUS_ID}`);
  logGlobal(`[INFO] Review status ID (configured): ${CONFIGURED_REVIEW_STATUS_ID}`);
  logGlobal(`[INFO] Review status ID (resolved): ${resolvedReviewStatusId}`);
  logGlobal(`[INFO] Review status name: ${REVIEW_STATUS_NAME}`);
  logGlobal(`[INFO] Poll seconds: ${POLL_SECONDS}`);
  logGlobal(`[INFO] Repo path: ${REPO_PATH}`);
  logGlobal(`[INFO] Claude bin: ${CLAUDE_BIN}`);

  while (true) {
    try {
      resolvedReviewStatusId = await resolveReviewStatusId();
      const readyTasks = await getTasksByStatus([READY_STATUS_ID]);
      const reviewTasks = await getTasksByStatus([resolvedReviewStatusId]);
      logGlobal(`[INFO] Poll result: ready=${readyTasks.length} review=${reviewTasks.length} review_status_id=${resolvedReviewStatusId}`);

      for (const task of readyTasks) {
        await processReadyTask(task, state);
      }

      for (const task of reviewTasks) {
        await processReviewTask(task, state);
      }
    } catch (error) {
      logGlobal(`[ERROR] Worker loop failed: ${String(error.message || error)}`);
    }

    await sleep(POLL_SECONDS * 1000);
  }
}

main().catch((error) => {
  console.error(`[CRITICAL] ${String(error.message || error)}`);
  process.exit(1);
});
