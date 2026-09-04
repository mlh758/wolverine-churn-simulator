#!/usr/bin/env bash
# Quantify assignment churn from the wolverine_node_records table and from
# agent start/stop log lines across all current + prior pods.
set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl -- --context=minikube"

PGPOD=$($KUBECTL get pod -l app=pg -o jsonpath='{.items[0].metadata.name}')

psql() {
  $KUBECTL exec "$PGPOD" -- psql -U postgres -d churnsim -qAt -c "$1"
}

echo "== node_records by event type =="
$KUBECTL exec "$PGPOD" -- psql -U postgres -d churnsim -c \
  "select event_name, count(*) from wolverine.wolverine_node_records group by 1 order by 2 desc;"

echo "== AssignmentChanged rows per minute =="
$KUBECTL exec "$PGPOD" -- psql -U postgres -d churnsim -c \
  "select date_trunc('minute', timestamp) as minute, count(*)
     from wolverine.wolverine_node_records
    where event_name = 'AssignmentChanged'
    group by 1 order by 1;"

echo "== currently registered nodes =="
$KUBECTL exec "$PGPOD" -- psql -U postgres -d churnsim -c \
  "select node_number, description from wolverine.wolverine_nodes order by node_number;"

echo "== current agent assignments per node =="
$KUBECTL exec "$PGPOD" -- psql -U postgres -d churnsim -c \
  "select node_id, count(*) from wolverine.wolverine_node_assignments group by 1;"

echo "== agent starts/stops seen in live pod logs =="
for pod in $($KUBECTL get pods -l app=churnsim -o jsonpath='{.items[*].metadata.name}'); do
  starts=$($KUBECTL logs "$pod" 2>/dev/null | grep -c 'AGENT-START' || true)
  stops=$($KUBECTL logs "$pod" 2>/dev/null | grep -c 'AGENT-STOP' || true)
  echo "$pod: starts=$starts stops=$stops"
done

echo
echo "total AssignmentChanged: $(psql "select count(*) from wolverine.wolverine_node_records where event_name = 'AssignmentChanged'")"
echo "total AgentStarted:      $(psql "select count(*) from wolverine.wolverine_node_records where event_name = 'AgentStarted'")"
echo "total AgentStopped:      $(psql "select count(*) from wolverine.wolverine_node_records where event_name = 'AgentStopped'")"
