#!/usr/bin/env bash
# Build the app image, load it into minikube, and deploy postgres + 3 app replicas.
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl --"

echo "== Building image =="
podman build -t localhost/churnsim:local .

echo "== Loading image into minikube =="
podman save localhost/churnsim:local -o /tmp/churnsim-local.tar
minikube image load /tmp/churnsim-local.tar
rm -f /tmp/churnsim-local.tar

echo "== Deploying postgres =="
$KUBECTL apply -f k8s/postgres.yaml
$KUBECTL rollout status deployment/pg --timeout=180s

echo "== Deploying churnsim (3 replicas) =="
$KUBECTL apply -f k8s/churnsim.yaml
$KUBECTL rollout status deployment/churnsim --timeout=300s

$KUBECTL get pods -o wide
