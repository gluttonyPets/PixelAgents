const EVENTS_URL = window.PIXELAGENTS_EVENTS_URL;

let lastHash = "";
let autoScroll = true;
let currentData = null;
let enabledFilters = new Set();

function escapeHtml(str) {
  return String(str ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function icon(kind, status) {
  if (kind === "assistant") return "💬";
  if (kind === "tool") return "🔧";
  if (kind === "tool_result" && status === "error") return "❌";
  if (kind === "tool_result") return "✅";
  if (kind === "system") return "⚙️";
  if (kind === "result") return "🏁";
  return "•";
}

function eventKey(ev) {
  if (ev.kind === "tool") return ev.meta?.tool || ev.title || "tool";
  if (ev.kind === "tool_result") return ev.meta?.tool || "tool_result";
  if (ev.kind === "system") return "system";
  if (ev.kind === "result") return "result";
  return ev.kind || "event";
}

function isMainEvent(ev) {
  return ev.kind === "assistant" || ev.kind === "result";
}

function isTechEvent(ev) {
  return !isMainEvent(ev);
}

function renderEvent(ev, idx) {
  const kind = ev.kind || "event";
  const status = ev.status || "info";
  const title = ev.title || kind;
  const body = ev.body || "";
  const ts = ev.ts ? new Date(ev.ts * 1000).toLocaleTimeString() : "";
  const key = eventKey(ev);

  return `
    <section class="event ${escapeHtml(kind)} ${escapeHtml(status)}">
      <div class="event-header">
        <span>${icon(kind, status)} ${escapeHtml(title)}</span>
        <span>#${idx + 1} · ${escapeHtml(key)} · ${escapeHtml(ts)}</span>
      </div>
      <div class="event-body">${escapeHtml(body)}</div>
    </section>
  `;
}

function buildFilters(techEvents) {
  const filtersEl = document.getElementById("filters");
  const keys = [...new Set(techEvents.map(eventKey))].sort((a, b) => a.localeCompare(b));

  if (!enabledFilters.size) {
    keys.forEach((key) => enabledFilters.add(key));
  } else {
    const currentInputs = [...filtersEl.querySelectorAll("input")].map((input) => input.value);

    keys.forEach((key) => {
      if (!currentInputs.includes(key)) {
        enabledFilters.add(key);
      }
    });
  }

  filtersEl.innerHTML = keys.map((key) => `
    <label class="chip">
      <input
        type="checkbox"
        value="${escapeHtml(key)}"
        ${enabledFilters.has(key) ? "checked" : ""}
        onchange="toggleFilter(this.value, this.checked)"
      />
      ${escapeHtml(key)}
    </label>
  `).join("");
}

function toggleFilter(key, enabled) {
  if (enabled) {
    enabledFilters.add(key);
  } else {
    enabledFilters.delete(key);
  }

  renderCurrent();
}

function renderCurrent() {
  if (!currentData) return;
  render(currentData, false);
}

function render(data, rebuildFilters = true) {
  currentData = data;

  const status = data.status || "running";
  const events = data.events || [];

  const mainEvents = events.filter(isMainEvent);
  let techEvents = events.filter(isTechEvent);

  if (rebuildFilters) {
    buildFilters(techEvents);
  }

  const search = document.getElementById("search").value.trim().toLowerCase();

  techEvents = techEvents.filter((ev) => {
    const key = eventKey(ev);
    const matchesFilter = enabledFilters.has(key);
    const haystack = `${ev.kind} ${ev.title} ${ev.body} ${key}`.toLowerCase();
    const matchesSearch = !search || haystack.includes(search);
    return matchesFilter && matchesSearch;
  });

  const statusPill = document.getElementById("status-pill");
  statusPill.textContent = "estado: " + status;
  statusPill.className = "pill " + (
    status === "done" ? "done" :
    status === "error" ? "error" :
    "running"
  );

  document.getElementById("updated-pill").textContent =
    "actualizado: " + new Date((data.updated_at || Date.now() / 1000) * 1000).toLocaleTimeString();

  document.getElementById("events-pill").textContent = "eventos: " + events.length;
  document.getElementById("main-pill").textContent = "principal: " + mainEvents.length;
  document.getElementById("tech-pill").textContent = "técnico: " + techEvents.length;

  const mainEl = document.getElementById("main-events");

  if (!mainEvents.length) {
    mainEl.className = "empty";
    mainEl.innerHTML = "Esperando mensajes humanos de Claude...";
  } else {
    mainEl.className = "";
    mainEl.innerHTML = mainEvents.map(renderEvent).join("");
  }

  const techEl = document.getElementById("tech-events");

  if (!techEvents.length) {
    techEl.className = "empty";
    techEl.innerHTML = "Sin eventos técnicos con los filtros actuales.";
  } else {
    techEl.className = "";
    techEl.innerHTML = techEvents.map(renderEvent).join("");
  }

  if (autoScroll) {
    const mainScroll = document.getElementById("main-scroll");
    const techScroll = document.getElementById("tech-scroll");

    if (mainScroll) {
      mainScroll.scrollTo({
        top: mainScroll.scrollHeight,
        behavior: "smooth",
      });
    }

    if (techScroll) {
      techScroll.scrollTo({
        top: techScroll.scrollHeight,
        behavior: "smooth",
      });
    }
  }
}

async function tick() {
  try {
    const res = await fetch(EVENTS_URL + "?t=" + Date.now(), {
      cache: "no-store",
    });

    if (!res.ok) {
      throw new Error("HTTP " + res.status);
    }

    const text = await res.text();
    const hash = String(text.length) + ":" + text.slice(-100);

    if (hash !== lastHash) {
      lastHash = hash;
      render(JSON.parse(text), true);
    }
  } catch (err) {
    document.getElementById("updated-pill").textContent = "error: " + err.message;
  }
}

function toggleAutoScroll() {
  autoScroll = !autoScroll;
  document.getElementById("autoscroll-label").textContent = autoScroll ? "ON" : "OFF";
}

tick();
setInterval(tick, 2000);
