#!/usr/bin/env bash
# Trigger a rolling deploy -- new pod template hash, same image -- the way a CD pipeline replaces
# pods, and wait for it.
#
# DEPENDS ON  a deployed churnsim cluster.
# REQUIRES    nothing.
# PRODUCES    3 replaced pods. MUTATES the cluster. Sets ROLLOUT_STAMP, which is what makes the
#             template hash change without a code change.
#
# ARGUMENTS
#
#   ./scripts/rollout.sh [stamp]   default: the current epoch seconds
set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl -- --context=minikube"

STAMP="${1:-$(date +%s)}"
echo "== Rolling deploy with stamp $STAMP =="
$KUBECTL set env deployment/churnsim ROLLOUT_STAMP="$STAMP"
$KUBECTL rollout status deployment/churnsim --timeout=600s
$KUBECTL get pods -l app=churnsim
