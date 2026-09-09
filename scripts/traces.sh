#!/usr/bin/env bash
# Pull Wolverine's own spans out of Jaeger for a run window and summarise the agent-assignment
# path — the question state sampling cannot answer: when the leader dispatches a batch, how long
# until it is confirmed, and where does the wall-clock actually go?
#
#   ./scripts/traces.sh                 # last 30 minutes
#   ./scripts/traces.sh 90              # last 90 minutes
#   ./scripts/traces.sh 30 raw > t.json # keep the raw payload
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl -- --context=minikube"

MINUTES="${1:-30}"
MODE="${2:-summary}"
LOOKBACK_US=$(( MINUTES * 60 * 1000000 ))

POD=$($KUBECTL get pod -l app=jaeger -o jsonpath='{.items[0].metadata.name}')
[ -n "$POD" ] || { echo "no jaeger pod -- kubectl apply -f k8s/jaeger.yaml" >&2; exit 2; }

fetch() {
    $KUBECTL exec "$POD" -- wget -qO- \
        "http://localhost:16686/api/traces?service=churnsim&lookback=${MINUTES}m&limit=2000&operation=$1" 2>/dev/null
}

if [ "$MODE" = "raw" ]; then
    fetch "wolverine_node_assignments"
    exit 0
fi

echo "== operations seen by Jaeger (service=churnsim, last ${MINUTES}m) =="
$KUBECTL exec "$POD" -- wget -qO- "http://localhost:16686/api/operations?service=churnsim" 2>/dev/null \
    | python3 -c '
import json,sys
d=json.load(sys.stdin).get("data") or []
names=sorted({o["name"] if isinstance(o,dict) else o for o in d})
print("\n".join(f"  {n}" for n in names))' \
    || echo "  (none - is SIM_OTLP_ENDPOINT set on the churnsim deployment?)"

echo
echo "== wolverine_node_assignments span durations =="
fetch "wolverine_node_assignments" | python3 -c '
import json, sys
try:
    d = json.load(sys.stdin)
except Exception:
    print("  (no traces returned)"); raise SystemExit
spans = [(s["duration"]/1000.0, s["operationName"], t["traceID"])
         for t in d.get("data", []) for s in t.get("spans", [])]
if not spans:
    print("  (no spans)"); raise SystemExit
spans.sort(reverse=True)
print(f"  {len(spans)} spans; slowest:")
for ms, op, tid in spans[:15]:
    print(f"    {ms:9.1f} ms  {op}  trace={tid}")
ds = sorted(s[0] for s in spans)
def pct(p): return ds[min(len(ds)-1, int(len(ds)*p))]
print(f"  p50 {pct(.5):.1f} ms   p95 {pct(.95):.1f} ms   max {ds[-1]:.1f} ms")
'
