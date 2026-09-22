#!/usr/bin/env bash
# Assignment churn, from the store's node-record history and from agent start/stop lines in the
# live pods' logs.
#
# DEPENDS ON  a deployed churnsim cluster (any arm). The RavenDB arm additionally needs
#             ./scripts/monitor.sh deploy.
# REQUIRES    nothing; reports whatever state the cluster is in.
# PRODUCES    a report on stdout. Reads only -- nothing is mutated.
#
# ARGUMENTS   none.
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"
source scripts/backend.sh

echo "== backend: $(sim_backend) =="

echo
echo "== node records by event type =="
# Captured once and reused for the totals below. On RavenDB this is a full scan of the
# NodeRecords collection, and a long run has tens of thousands of them -- asking twice is a
# minute of wall clock for an answer we already have.
records=$(db_records)
echo "$records"

echo
echo "== AssignmentChanged rows per minute =="
db_per_minute AssignmentChanged

echo
echo "== currently registered nodes =="
db_nodes

echo
echo "== current agent assignments per node =="
db_per_node

echo
echo "== agent starts/stops seen in live pod logs =="
for pod in $($K get pods -l app=churnsim -o jsonpath='{.items[*].metadata.name}'); do
  starts=$($K logs "$pod" 2>/dev/null | grep -c 'AGENT-START' || true)
  stops=$($K logs "$pod" 2>/dev/null | grep -c 'AGENT-STOP' || true)
  echo "$pod: starts=$starts stops=$stops"
done

# The durable record is the source of truth: the logs of replaced pods are gone.
echo
for event in AssignmentChanged AgentStarted AgentStopped; do
  count=$(awk -F'\t' -v e="$event" '$1 == e { print $2 }' <<<"$records")
  printf 'total %-18s %s\n' "$event:" "${count:-0}"
done
