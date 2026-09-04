#!/usr/bin/env bash
# Clear the node_records history so the next measurement window starts at zero.
# (Leaves nodes/assignments untouched -- the cluster keeps running.)
set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl -- --context=minikube"

PGPOD=$($KUBECTL get pod -l app=pg -o jsonpath='{.items[0].metadata.name}')
$KUBECTL exec "$PGPOD" -- psql -U postgres -d churnsim -c \
  "truncate wolverine.wolverine_node_records;"
echo "node_records truncated"
