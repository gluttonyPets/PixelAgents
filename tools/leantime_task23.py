import urllib.request
import json

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


# Step 1: Read ticket 23
print("=== STEP 1: Reading ticket 23 ===")
result = call("leantime.rpc.tickets.getTicket", {"id": 23}, call_id=1)
print(json.dumps(result, indent=2, ensure_ascii=False))
