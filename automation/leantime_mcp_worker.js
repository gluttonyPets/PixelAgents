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
const REVIEW_STATUS_ID = Number(process.env.REVIEW_STATUS_ID || "5");
const POLL_SECONDS = Number(process.env.POLL_SECONDS || "30");
const REPO_PATH = process.env.PIXELAGENTS_REPO || "/home/debian/Proyectos/PixelAgents";
const LOG_DIR = process.env.WORKER_LOG_DIR || path.join(REPO_PATH, "automation", "logs");
const STATE_FILE = process.env.WORKER_STATE_FILE || path.join(resolveHome(), ".config", "pixelagents", "leantime-mcp-worker-state.json");
const CLAUDE_BIN = process.env.CLAUDE_BIN || "claude";
const CLAUDE_MODEL = process.env.CLAUDE_MODEL || "";
const CLAUDE_MAX_TURNS = Number(process.env.CLAUDE_MAX_TURNS || "80");
const CLAUDE_NOTE_PREFIX = "[CLAUDE_AUTOMATION]";

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

async function updateTicketStatus(ticketId, statusId) {
  const ticket = await getTicket(ticketId);
  if (!ticket) {
    throw new Error(`Could not read ticket ${ticketId} before updating status.`);
  }

  const projectId = Number(ticket.projectId || PROJECT_ID);
  const attempts = [
    { id: Number(ticketId), values: { status: Number(statusId), projectId } },
    { values: { id: Number(ticketId), status: Number(statusId), projectId } },
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

async function getTicketComments(ticketId) {
  const attempts = [
    { moduleId: Number(ticketId), commentModule: "tickets" },
    { moduleId: String(ticketId), commentModule: "tickets" },
  ];

  for (const params of attempts) {
    for (const method of ["leantime.rpc.comments.getComments", "leantime.rpc.comments.getAll"]) {
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
      } catch {
        continue;
      }
    }
  }

  return [];
}

function getCommentBody(comment) {
  return String(comment.comment || comment.content || comment.description || "").trim();
}

function getCommentId(comment) {
  return String(comment.id || comment.commentId || comment.pk || "");
}

function isAutomationComment(comment) {
  return getCommentBody(comment).startsWith(CLAUDE_NOTE_PREFIX);
}

function sortComments(comments) {
  return [...comments].sort((a, b) => {
    const aId = Number(getCommentId(a) || 0);
    const bId = Number(getCommentId(b) || 0);
    if (aId !== bId) {
      return aId - bId;
    }
    return String(a.dateModified || a.dateCreated || "").localeCompare(String(b.dateModified || b.dateCreated || ""));
  });
}

function commentSignature(comment) {
  const id = getCommentId(comment);
  const timestamp = String(comment.dateModified || comment.dateCreated || "");
  const preview = getCommentBody(comment).replace(/\s+/g, " ").slice(0, 140);
  const parent = String(comment.commentParent || "");
  return `${id}|${timestamp}|${parent}|${preview}`;
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

function commentText(comment) {
  const id = getCommentId(comment) || "desconocido";
  const author = String(comment.fullName || comment.name || comment.user || comment.author || "desconocido");
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
    reviewBlock,
    sessionBlock,
    "",
    "Objetivos obligatorios:",
    "- Al empezar, mueve la tarea a In Progress si todavía no lo está.",
    "- Si el trabajo no es trivial, crea subtareas reales en Leantime.",
    "- Mantén el estado real del trabajo sincronizado con Leantime.",
    "- Si el trabajo acaba correctamente, deja la tarea en Review.",
    "- Si la corrección viene de un comentario concreto, responde en ese hilo y marca claramente que ya quedó resuelto.",
    "- No marques Done sin aprobación humana.",
    "- No hagas merge a develop ni master.",
    "",
    "Devuelve también un breve resumen final en tu resultado.",
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
    writeJson(this.eventsPath, {
      ticket_id: this.ticketId,
      headline: this.headline,
      status: this.status,
      started_at: this.startedAt / 1000,
      updated_at: Date.now() / 1000,
      stats: this.stats,
      events: this.events,
    });

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
  }
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

    child.on("error", reject);
    child.on("close", (code) => {
      if (stderr.trim()) {
        logger.log("system", "Claude stderr", stderr.trim());
      }

      if (code !== 0) {
        return reject(new Error(`claude exited with code ${code}\n${stderr}`));
      }

      try {
        const parsed = JSON.parse(stdout);
        return resolve(parsed);
      } catch (error) {
        return reject(new Error(`Could not parse Claude JSON output: ${error.message}\n${stdout}`));
      }
    });
  });
}

async function processReadyTask(task, state) {
  const ticketId = Number(task.id);
  if (state.processedReadyTasks.includes(ticketId)) {
    return;
  }

  logGlobal(`[INFO] Found Ready ticket ${ticketId}: ${task.headline || ""}`);
  await updateTicketStatus(ticketId, IN_PROGRESS_STATUS_ID);

  const logger = new TicketLogger(ticketId, String(task.headline || "(sin titulo)"));
  try {
    const result = await runClaude(buildPrompt(task, "ready", [], state.ticketSessions[String(ticketId)] || ""), state.ticketSessions[String(ticketId)] || "", logger);
    logger.setResult(result);

    state.processedReadyTasks = Array.from(new Set([...state.processedReadyTasks, ticketId])).sort((a, b) => a - b);
    if (result.session_id) {
      state.ticketSessions[String(ticketId)] = result.session_id;
    }

    const refreshed = await getTicket(ticketId);
    const status = refreshed ? Number(refreshed.status || -1) : -1;
    if (status === READY_STATUS_ID || status === IN_PROGRESS_STATUS_ID) {
      logGlobal(`[INFO] Ticket ${ticketId} finished without Review status. Moving it to Review.`);
      await updateTicketStatus(ticketId, REVIEW_STATUS_ID);
    }
  } catch (error) {
    logger.log("error", "Claude execution failed", String(error.message || error));
    logGlobal(`[ERROR] ${String(error.message || error)}`);
  } finally {
    saveState(state);
  }
}

async function processReviewTask(task, state) {
  const ticketId = Number(task.id);
  const allComments = await getTicketComments(ticketId);
  const comments = allComments.filter((comment) => !isAutomationComment(comment));
  const latestSignature = latestCommentSignature(comments);
  const knownSignature = state.reviewCommentMarkers[String(ticketId)] || "";
  const previousSessionId = state.ticketSessions[String(ticketId)] || "";

  logGlobal(`[INFO] Review scan ticket=${ticketId} comments=${comments.length} known_signature=${knownSignature ? "yes" : "no"} latest_signature=${latestSignature ? "yes" : "no"} previous_session=${previousSessionId ? "yes" : "no"} title=${task.headline || ""}`);

  if (!latestSignature) {
    return;
  }

  if (!knownSignature) {
    state.reviewCommentMarkers[String(ticketId)] = latestSignature;
    saveState(state);
    return;
  }

  if (latestSignature === knownSignature) {
    return;
  }

  const pendingComments = commentsAfterSignature(comments, knownSignature);
  logGlobal(`[INFO] Found new Review feedback for ticket ${ticketId}: ${task.headline || ""}`);

  await updateTicketStatus(ticketId, IN_PROGRESS_STATUS_ID);

  const logger = new TicketLogger(ticketId, String(task.headline || "(sin titulo)"));
  try {
    const result = await runClaude(buildPrompt(task, "review_feedback", pendingComments, previousSessionId), previousSessionId, logger);
    logger.setResult(result);

    if (result.session_id) {
      state.ticketSessions[String(ticketId)] = result.session_id;
    }

    const refreshedComments = (await getTicketComments(ticketId)).filter((comment) => !isAutomationComment(comment));
    const refreshedSignature = latestCommentSignature(refreshedComments);
    if (refreshedSignature) {
      state.reviewCommentMarkers[String(ticketId)] = refreshedSignature;
    }
  } catch (error) {
    logger.log("error", "Claude execution failed", String(error.message || error));
    logGlobal(`[ERROR] ${String(error.message || error)}`);
  } finally {
    saveState(state);
  }
}

async function main() {
  if (!LEANTIME_API_KEY) {
    throw new Error("Missing LEANTIME_API_KEY");
  }

  ensureDir(LOG_DIR);
  const state = loadState();

  logGlobal("[INFO] PixelAgents Leantime MCP worker started");
  logGlobal(`[INFO] Project ID: ${PROJECT_ID}`);
  logGlobal(`[INFO] Ready status ID: ${READY_STATUS_ID}`);
  logGlobal(`[INFO] In Progress status ID: ${IN_PROGRESS_STATUS_ID}`);
  logGlobal(`[INFO] Review status ID: ${REVIEW_STATUS_ID}`);
  logGlobal(`[INFO] Poll seconds: ${POLL_SECONDS}`);
  logGlobal(`[INFO] Repo path: ${REPO_PATH}`);
  logGlobal(`[INFO] Claude bin: ${CLAUDE_BIN}`);

  while (true) {
    try {
      const readyTasks = await getTasksByStatus([READY_STATUS_ID]);
      const reviewTasks = await getTasksByStatus([REVIEW_STATUS_ID]);
      logGlobal(`[INFO] Poll result: ready=${readyTasks.length} review=${reviewTasks.length}`);

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
