#!/usr/bin/env bash
# Trigger a rolling deploy (new pod template hash, same image) and wait for it,
# mimicking `kubectl rollout restart` the way a CD pipeline replaces pods.
set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl --"

STAMP="${1:-$(date +%s)}"
echo "== Rolling deploy with stamp $STAMP =="
$KUBECTL set env deployment/churnsim ROLLOUT_STAMP="$STAMP"
$KUBECTL rollout status deployment/churnsim --timeout=600s
$KUBECTL get pods -l app=churnsim
