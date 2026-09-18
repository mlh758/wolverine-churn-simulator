#!/usr/bin/env bash
# Build the app image against a chosen Wolverine version and message store, load it into
# minikube, and deploy the store plus 3 app replicas running exactly that image.
#
#   ./scripts/deploy.sh                                   # released WolverineFx 5.39.0, postgres
#   ./scripts/deploy.sh 6.33.0-stock.1                    # pristine main, packed into localfeed/
#   ./scripts/deploy.sh 6.33.0-proposal.1                 # GH-3987/GH-3959 implementation
#   ./scripts/deploy.sh 6.39.0 --backend ravendb          # the RavenDB arm
#
# The backend is a BUILD-TIME choice (ChurnSim.csproj picks the message store package and the
# wiring file from it), so switching arms rebuilds the image. The deployment declares the same
# value in SIM_BACKEND and ChurnSim refuses to start on a mismatch.
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl -- --context=minikube"

VERSION="5.39.0"
BACKEND="postgres"

while [ $# -gt 0 ]; do
  case "$1" in
    --backend) BACKEND="${2:?--backend needs postgres or ravendb}"; shift 2 ;;
    -*) echo "deploy.sh: unknown option $1" >&2; exit 2 ;;
    *) VERSION="$1"; shift ;;
  esac
done

case "$BACKEND" in
  postgres) STORE_MANIFEST="k8s/postgres.yaml"; STORE_LABEL="pg"; APP_MANIFEST="k8s/churnsim.yaml" ;;
  ravendb)  STORE_MANIFEST="k8s/ravendb.yaml";  STORE_LABEL="ravendb"; APP_MANIFEST="k8s/churnsim-ravendb.yaml" ;;
  *) echo "deploy.sh: --backend must be 'postgres' or 'ravendb', not '$BACKEND'" >&2; exit 2 ;;
esac

# The RavenDB arm needs the native ravendb:// control queue for Balanced-mode agent commands.
# It landed in Wolverine on 2026-07-02, so nothing in the 5.x line has it, and there is no
# fallback: UseTcpForControlEndpoint advertises tcp://localhost, which no peer pod can reach.
# ChurnSim checks this at startup too; catching it here saves a build and a crash loop.
if [ "$BACKEND" = "ravendb" ] && [[ "$VERSION" == 5.* ]]; then
  echo "deploy.sh: WolverineFx.RavenDb $VERSION predates the native RavenDB control queue," >&2
  echo "           so Balanced durability cannot elect a control endpoint. Use a 6.x build." >&2
  exit 2
fi

# The image tag carries the backend, so two arms cannot collide in the local image store and a
# `podman images` listing says which is which.
TAG="localhost/churnsim:$VERSION-$BACKEND"

echo "== Building image for Wolverine $VERSION on $BACKEND =="
podman build \
  --build-arg WOLVERINE_VERSION="$VERSION" \
  --build-arg SIM_BACKEND="$BACKEND" \
  -t "$TAG" -t localhost/churnsim:local .

echo "== Loading image into minikube =="
podman save "$TAG" -o /tmp/churnsim-local.tar
minikube image load /tmp/churnsim-local.tar
rm -f /tmp/churnsim-local.tar

echo "== Deploying $BACKEND =="
$KUBECTL apply -f "$STORE_MANIFEST"
$KUBECTL rollout status "deployment/$STORE_LABEL" --timeout=300s

echo "== Deploying churnsim (3 replicas) on $TAG =="
$KUBECTL apply -f "$APP_MANIFEST"
$KUBECTL set image deployment/churnsim churnsim="$TAG"
$KUBECTL rollout status deployment/churnsim --timeout=300s

$KUBECTL get pods -o wide

if [ "$BACKEND" = "ravendb" ]; then
  echo
  echo "note: the RavenDB arm reads the store through the safetylab pod (there is no psql to"
  echo "      exec into), so measure.sh and the experiment scripts need it deployed:"
  echo "        ./scripts/monitor.sh deploy"
fi
