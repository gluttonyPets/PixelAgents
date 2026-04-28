import urllib.request
import json
import sys

URL = "http://127.0.0.1:8090/api/jsonrpc"
API_KEY = "lt_6IBAU9ATBOixaF3cOQO2aoMtNdWiAKSL_aIMT77Hr87em1ONl6uxGbZZCqz7q36xe"

HEADERS = {
    "Content-Type": "application/json",
    "x-api-key": API_KEY
}

def call(method, params, call_id=1):
    payload = {
        "jsonrpc": "2.0",
        "method": method,
        "params": params,
        "id": call_id
    }
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(URL, data=data, headers=HEADERS, method="POST")
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))


# ─── Step 1: Read ticket 23 ───────────────────────────────────────────────────
print("=" * 60)
print("STEP 1: Reading ticket 23")
print("=" * 60)
read_result = call("leantime.rpc.tickets.getTicket", {"id": 23})
print(json.dumps(read_result, indent=2, ensure_ascii=False))

ticket = read_result.get("result", {})
print("\n--- Key fields ---")
for field in ["id", "headline", "status", "type", "projectId", "description",
              "sprint", "milestoneid", "dateToFinish", "editorId", "dependingTicketId"]:
    print(f"  {field}: {ticket.get(field, '(not present)')}")


# ─── Step 2: Update status to 4 (In Progress) ────────────────────────────────
print("\n" + "=" * 60)
print("STEP 2: Updating ticket 23 status to 4 (In Progress)")
print("=" * 60)

# Build update params carrying all existing fields plus the new status
update_params = dict(ticket)
update_params["status"] = "4"
update_params["id"] = 23

update_result = call("leantime.rpc.tickets.updateTicket", update_params, call_id=2)
print(json.dumps(update_result, indent=2, ensure_ascii=False))


# ─── Step 3: Add technical plan comment ──────────────────────────────────────
print("\n" + "=" * 60)
print("STEP 3: Adding technical plan comment")
print("=" * 60)

comment_text = """## Plan técnico – Tarea 23 "Tablas más chulas"

Rama: feature/apikeys-tabla-visual

### Objetivo
Mejorar visualmente la tabla de API Keys en /apikeys:
- Añadir filtros por proveedor (pills/chips)
- Añadir buscador por nombre
- Mostrar ID corto de la clave (primeros 8 chars del GUID)
- Mostrar máscara de la key (•••••••••)
- Rediseño visual: mejor espaciado, hover, iconos
- Resumen por proveedor con recuento en los filtros

### Archivos afectados
- Client/Pages/ApiKeys.razor
- Client/wwwroot/css/app.css

### Agentes involucrados
- frontend-implementer: implementación visual
- test-runner: build y lint
- code-reviewer: revisión final

### Sin cambios en backend
No se requieren cambios en Server/ ni en Dtos."""

comment_params = {
    "moduleId": 23,
    "commentModule": "tickets",
    "comment": comment_text,
    "commentParent": ""
}

comment_result = call("leantime.rpc.comments.addComment", comment_params, call_id=3)
print(json.dumps(comment_result, indent=2, ensure_ascii=False))

print("\nDone.")
