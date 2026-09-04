#!/usr/bin/env bash
# Drop the wolverine schema entirely (nodes, assignments, records, envelopes) and
# bounce the app pods so the next variant starts from a clean slate. Use between
# runs of DIFFERENT Wolverine builds.
set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl -- --context=minikube"

PGPOD=$($KUBECTL get pod -l app=pg -o jsonpath='{.items[0].metadata.name}')
$KUBECTL exec "$PGPOD" -- psql -U postgres -d churnsim -c "drop schema if exists wolverine cascade;"
$KUBECTL rollout restart deployment/churnsim
$KUBECTL rollout status deployment/churnsim --timeout=300s
echo "schema dropped and pods bounced"
