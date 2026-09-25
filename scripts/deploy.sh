#!/usr/bin/env bash
# Build the app image against a Wolverine version and message store, load it into minikube, and
# deploy the store plus 3 app replicas running exactly that image.
#
# DEPENDS ON  podman, minikube, and nuget.org for a released version (localfeed/ for a local one).
# REQUIRES    a running minikube. A 5.x version with --backend ravendb is refused: the native
#             ravendb:// control queue landed 2026-07-02 and Balanced durability cannot elect a
#             control endpoint without it.
# PRODUCES    a deployed store and churnsim at localhost/churnsim:<version>-<backend>. MUTATES the
#             cluster. The RavenDB arm additionally needs ./scripts/monitor.sh deploy to be
#             measurable at all.
#
# The backend is a BUILD-TIME choice -- ChurnSim.csproj picks the message store package and the
# wiring file from it -- so switching arms rebuilds the image. The deployment declares the same
# value in SIM_BACKEND and ChurnSim refuses to start on a mismatch.
#
# ARGUMENTS
#
#   ./scripts/deploy.sh [version] [--backend postgres|mysql|ravendb] [--topology single|cluster]
#
#     version   defaults to the contents of ./wolverine-version, which is also what
#               Directory.Build.props and the Dockerfile read. Change the version under test by
#               editing that file; pass one here to override for a single run.
#
#     --topology cluster   RavenDB only: the three-member store for the partition experiment (E7),
#               k8s/ravendb-cluster.yaml, with one PINNED churnsim pod per member (a StatefulSet,
#               k8s/churnsim-ravendb-cluster.yaml) and the per-member monitor. The order here is
#               load-bearing: store, then monitor, then `raven-cluster form` THROUGH the monitor
#               (bootstrap, add members, create the database at factor 3), then churnsim — because
#               a churnsim pod that finds no database creates one at factor 1, and three servers
#               with a factor-1 database is not a replicated store.
set -euo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl -- --context=minikube"

# One source of truth: ./wolverine-version. Directory.Build.props and the Dockerfile read the
# same file, so there is no second copy of the number to drift.
VERSION_FILE="wolverine-version"
[ -r "$VERSION_FILE" ] || { echo "deploy.sh: $VERSION_FILE is missing" >&2; exit 2; }
VERSION="$(tr -d '[:space:]' < "$VERSION_FILE")"
[ -n "$VERSION" ] || { echo "deploy.sh: $VERSION_FILE is empty" >&2; exit 2; }
BACKEND="postgres"
TOPOLOGY="single"

while [ $# -gt 0 ]; do
  case "$1" in
    --backend) BACKEND="${2:?--backend needs postgres, mysql or ravendb}"; shift 2 ;;
    --topology) TOPOLOGY="${2:?--topology needs single or cluster}"; shift 2 ;;
    -*) echo "deploy.sh: unknown option $1" >&2; exit 2 ;;
    *) VERSION="$1"; shift ;;
  esac
done

case "$BACKEND/$TOPOLOGY" in
  postgres/single) STORE_MANIFEST="k8s/postgres.yaml"; STORE_LABEL="pg"; APP_MANIFEST="k8s/churnsim.yaml" ;;
  mysql/single)    STORE_MANIFEST="k8s/mysql.yaml";    STORE_LABEL="mysql"; APP_MANIFEST="k8s/churnsim-mysql.yaml" ;;
  ravendb/single)  STORE_MANIFEST="k8s/ravendb.yaml";  STORE_LABEL="ravendb"; APP_MANIFEST="k8s/churnsim-ravendb.yaml" ;;
  ravendb/cluster) STORE_MANIFEST="k8s/ravendb-cluster.yaml"; STORE_LABEL="ravendb"; APP_MANIFEST="k8s/churnsim-ravendb-cluster.yaml" ;;
  postgres/cluster|mysql/cluster) echo "deploy.sh: --topology cluster is RavenDB only (the RDBMS stores are a single node here by design)" >&2; exit 2 ;;
  *) echo "deploy.sh: --backend must be 'postgres', 'mysql' or 'ravendb' and --topology 'single' or 'cluster', not '$BACKEND'/'$TOPOLOGY'" >&2; exit 2 ;;
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

# The replicated store needs a RavenDB license before it can be formed: an unlicensed server
# allows one node and refuses to add a second (402 LicenseLimitException). Refuse now, before a
# multi-minute image build, rather than after forming one member and calling it a cluster. The
# license is a registration against an email address on ravendb.net, so it cannot be fetched here.
if [ "$TOPOLOGY" = "cluster" ] && ! $KUBECTL get secret ravendb-license >/dev/null 2>&1; then
  echo "deploy.sh: --topology cluster needs a RavenDB license, and Secret 'ravendb-license' does not exist." >&2
  echo "           An unlicensed RavenDB runs one node and refuses to add members. Get the free Developer" >&2
  echo "           license (three nodes) from https://ravendb.net/license/request, then:" >&2
  echo "             just ravendb-license path/to/license.json" >&2
  echo "           and re-run this deploy. See k8s/ravendb-cluster.yaml." >&2
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

# ONE store at a time. Every arm reuses the workload name `churnsim` and the app image is built
# for exactly one store, so a store left behind by the previous arm is not merely dead weight: it
# still answers on its Service, and `sim_backend`'s last-resort fallback reads whichever store is
# PRESENT, so a stale one can make an unlabelled cluster report the wrong arm. Each store's own
# emptyDir goes with it, which is fine -- a store is dropped between builds anyway.
#
# Two topologies of the RavenDB store exist and neither converts into the other in place (a
# Service's clusterIP is immutable, and a Deployment is not a StatefulSet), so the wanted workload
# KIND is part of what makes a store "this arm's".
#
# Every delete below is keyed on the object existing, so re-deploying the same arm leaves its own
# Service alone -- deleting and recreating a headless Service mid-run would drop every member's
# DNS name for a moment.
drop_store() {
  local kind="$1" name="$2"
  if $KUBECTL get "$kind" "$name" >/dev/null 2>&1; then
    $KUBECTL delete "$kind" "$name" --wait=true
    $KUBECTL delete service "$name" --ignore-not-found
  fi
}

# `<workload kind>/<name>` for the store THIS deploy is about to create; everything else goes.
if [ "$TOPOLOGY" = "cluster" ]; then WANTED="statefulset/ravendb"; else WANTED="deployment/$STORE_LABEL"; fi

echo "== Clearing every store that is not $WANTED =="
for store in deployment/pg deployment/mysql deployment/ravendb statefulset/ravendb; do
  [ "$store" = "$WANTED" ] || drop_store "${store%%/*}" "${store#*/}"
done

# Likewise for churnsim itself: the replicated arm is a StatefulSet and the others a Deployment.
echo "== Clearing the churnsim workload of the other kind, if any =="
if [ "$TOPOLOGY" = "cluster" ]; then
  $KUBECTL delete deployment churnsim --ignore-not-found --wait=true
else
  if $KUBECTL get statefulset churnsim >/dev/null 2>&1; then
    $KUBECTL delete statefulset churnsim --wait=true
    $KUBECTL delete service churnsim --ignore-not-found
  fi
fi

echo "== Deploying $BACKEND ($TOPOLOGY) =="
$KUBECTL apply -f "$STORE_MANIFEST"
if [ "$TOPOLOGY" = "cluster" ]; then
  # A fresh store every deploy. The members read RAVEN_License from the Secret at START, and the
  # StatefulSet's OnDelete strategy means `apply` never replaces a running pod — so a license added
  # after the pods came up would sit unread while `form` failed on the same 402 as before. Deleting
  # the pods also empties their emptyDir, so the members come back passive and `form` rebuilds the
  # cluster from nothing; that is the same "drop the store between builds" rule the single arms
  # follow, made explicit.
  source scripts/backend.sh
  $KUBECTL delete pod -l app=ravendb --ignore-not-found --wait=true
  # Both StatefulSets use OnDelete, which `rollout status` refuses; wait on the pods instead.
  wait_ready app=ravendb 3 300

  # The monitor is how anything outside the cluster talks to RavenDB (there is no psql to exec
  # into), and forming the cluster is such a conversation. It reads the topology off the cluster
  # to pick its manifest, which is why the StatefulSet has to exist before it is deployed.
  echo "== Deploying the monitor (the cluster is formed through it) =="
  ./scripts/monitor.sh deploy

  echo "== Forming the RavenDB cluster and creating the database at replication factor 3 =="
  _raven raven-cluster form --replication-factor 3
else
  $KUBECTL rollout status "deployment/$STORE_LABEL" --timeout=300s
fi

# The node log tailer, before churnsim, so it is already following when the first pod writes its
# SIM-IDENTITY line. Idempotent; independent of the arm.
echo "== Deploying the node log tailer =="
./scripts/logtail.sh deploy

echo "== Deploying churnsim (3 replicas) on $TAG =="
$KUBECTL apply -f "$APP_MANIFEST"
if [ "$TOPOLOGY" = "cluster" ]; then
  # OnDelete update strategy: setting the image changes nothing running, so the pods are
  # deleted and come back on the new image. Deleting before they exist is a no-op.
  $KUBECTL set image statefulset/churnsim churnsim="$TAG"
  $KUBECTL delete pod -l app=churnsim --ignore-not-found --wait=true
  wait_ready app=churnsim 3 300
else
  $KUBECTL set image deployment/churnsim churnsim="$TAG"
  $KUBECTL rollout status deployment/churnsim --timeout=300s
fi

$KUBECTL get pods -o wide

if [ "$BACKEND" = "ravendb" ] && [ "$TOPOLOGY" = "single" ]; then
  echo
  echo "note: the RavenDB arm reads the store through the safetylab pod (there is no psql to"
  echo "      exec into), so measure.sh and the experiment scripts need it deployed:"
  echo "        ./scripts/monitor.sh deploy"
fi

if [ "$BACKEND" = "mysql" ]; then
  echo
  echo "note: measure.sh and the experiment scripts work on this arm with no monitor, because the"
  echo "      mysql client ships in the store's image. A SafetyLab CAPTURE still needs one:"
  echo "        ./scripts/monitor.sh deploy"
  echo
  echo "      E8 (just lock-kill) and E9 (just db-partition) are PostgreSQL-only and refuse here."
  echo "      MySQL's KILL on the connection holding wolverine_<lockid> is the same fault on the"
  echo "      same shape of lock, but nothing injects it yet."
fi

if [ "$TOPOLOGY" = "cluster" ]; then
  echo
  echo "note: the replicated arm is measured by ./scripts/split-brain.sh (just split-brain)."
  echo "      The monitor is already deployed and reads every member; the churnsim pods are"
  echo '      pinned churnsim-N -> ravendb-N. `just raven-cluster-status` shows the topology.'
fi
