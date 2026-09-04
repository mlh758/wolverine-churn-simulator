#!/usr/bin/env bash
# Build the app image against a chosen Wolverine version, load it into minikube,
# and deploy postgres + 3 app replicas running exactly that image.
#
#   ./scripts/deploy.sh                    # released WolverineFx 5.39.0
#   ./scripts/deploy.sh 6.33.0-stock.1     # pristine main, packed into localfeed/
#   ./scripts/deploy.sh 6.33.0-proposal.1  # GH-3987/GH-3959 implementation
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl -- --context=minikube"

VERSION="${1:-5.39.0}"
TAG="localhost/churnsim:$VERSION"

echo "== Building image for Wolverine $VERSION =="
podman build --build-arg WOLVERINE_VERSION="$VERSION" -t "$TAG" -t localhost/churnsim:local .

echo "== Loading image into minikube =="
podman save "$TAG" -o /tmp/churnsim-local.tar
minikube image load /tmp/churnsim-local.tar
rm -f /tmp/churnsim-local.tar

echo "== Deploying postgres =="
$KUBECTL apply -f k8s/postgres.yaml
$KUBECTL rollout status deployment/pg --timeout=180s

echo "== Deploying churnsim (3 replicas) on $TAG =="
$KUBECTL apply -f k8s/churnsim.yaml
$KUBECTL set image deployment/churnsim churnsim="$TAG"
$KUBECTL rollout status deployment/churnsim --timeout=300s

$KUBECTL get pods -o wide
