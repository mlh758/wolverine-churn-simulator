#!/usr/bin/env bash
# Dump every churnsim pod's raw JSON log into a run directory, one file per pod.
#
# No transformation: the pod already emits structured JSON (SIM_JSON_LOGS=true), so storing it
# verbatim keeps every field queryable by scripts/logq.sh. The old `harvest` step existed only to
# turn human-formatted text into records, and with JSON logging it has nothing left to do.
#
# Still a pull, so the standing caveat applies: `kubectl logs` cannot reach a pod that is already
# gone. Capture before the pods you care about are replaced, or accept that a replaced pod
# contributes nothing. Solving that properly needs a node-level collector, which this rig does not
# run -- see docs/harness-traps.md.
#
#   ./scripts/capture-logs.sh runs/foo
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

DIR="${1:?usage: capture-logs.sh <dir>}"
mkdir -p "$DIR"

n=0
for pod in $($K get pods -l app=churnsim -o json 2>/dev/null | python3 scripts/live_pods.py); do
    $K logs "$pod" > "$DIR/raw.$pod.jsonl" 2>/dev/null
    lines=$(wc -l < "$DIR/raw.$pod.jsonl")
    echo "  $pod: $lines lines"
    n=$(( n + 1 ))
done

[ "$n" -gt 0 ] || { echo "no live churnsim pods" >&2; exit 2; }
echo "captured $n pod log(s) into $DIR"
