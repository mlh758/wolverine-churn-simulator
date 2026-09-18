#!/usr/bin/env bash
# Dump every live churnsim pod's raw log into a directory, one file per pod.
#
# DEPENDS ON  a deployed churnsim cluster, $SAFETYLAB.
# REQUIRES    at least one live pod; exits 2 otherwise.
# PRODUCES    <dir>/raw.<pod>.jsonl, verbatim. Queryable with scripts/logq.sh.
#
# CAVEAT      `kubectl logs` cannot reach a pod that is already gone, so a pod replaced before this
#             runs contributes nothing. Capture before the pods you care about are replaced.
#
# ARGUMENTS
#
#   ./scripts/capture-logs.sh <dir>
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

# For $SAFETYLAB and require_safetylab. Live-pod selection is `safetylab pods`, which applies
# the Running AND Ready AND not-terminating rule that a phase selector alone does not.
source scripts/backend.sh
require_safetylab

DIR="${1:?usage: capture-logs.sh <dir>}"
mkdir -p "$DIR"

n=0
for pod in $("$SAFETYLAB" pods --label app=churnsim); do
    $K logs "$pod" > "$DIR/raw.$pod.jsonl" 2>/dev/null
    lines=$(wc -l < "$DIR/raw.$pod.jsonl")
    echo "  $pod: $lines lines"
    n=$(( n + 1 ))
done

[ "$n" -gt 0 ] || { echo "no live churnsim pods" >&2; exit 2; }
echo "captured $n pod log(s) into $DIR"
