#!/usr/bin/env bash
# Drop the store entirely (nodes, assignments, records, envelopes) and bounce the app pods so
# the next variant starts from a clean slate. Use between runs of DIFFERENT Wolverine builds.
#
# postgres: drop schema wolverine cascade
# ravendb:  hard-delete the churnsim database. ChurnSim recreates it on startup, which is why
#           bouncing the pods is not optional here -- without it nothing recreates the database
#           and every node sits in a restart loop.
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"
source scripts/backend.sh

backend=$(sim_backend)

db_drop
$K rollout restart deployment/churnsim
$K rollout status deployment/churnsim --timeout=300s
echo "store dropped ($backend) and pods bounced"
