#!/usr/bin/env bash
# The node log tailer: deploy it, or clear its copy between experiments.
#
# DEPENDS ON  minikube; the fluent-bit and busybox images come from Docker Hub on first use.
# REQUIRES    for `reset`: a live tailer pod. Exits 2 otherwise.
# PRODUCES    `deploy`: a running logtail DaemonSet, on the manifest's "unscoped" run. `reset`: EVERY
#             run directory under /data/podlogs deleted and the tailer restarted on its current run
#             name, re-reading every log the kubelet still has from the beginning. MUTATES the node's
#             copy: every earlier run is gone. Pull what matters first (`safetylab podlogs pull
#             <dir> --run <name>`).
#
# `deploy` is idempotent and deploy.sh runs it on every deploy, before churnsim, so the tailer is
# already following when the first pod writes its SIM-IDENTITY line. Note `kubectl apply` resets
# the run to "unscoped" (three-way merge; docs/harness-traps.md): a deploy is a new epoch.
#
# Starting a run is `safetylab podlogs start <name>` -- a decision (the name must be unused, the
# tailer must come back following every live pod), so it lives in SafetyLab. monitor.sh start and
# the kill scripts call it.
#
# RESET RESTARTS THE TAILER ON PURPOSE. Its offset database lives inside the run directory;
# deleting the directory under a running tailer would leave the open offsets, and the next lines
# of a still-live pod would land in a fresh file with no SIM-IDENTITY at the top -- a pod with no
# identity is invisible to every pod-side check. Restarting makes the tailer recreate the
# directory and read every file the kubelet still has from the head, so the live pods come back
# whole.
#
# ARGUMENTS
#
#   ./scripts/logtail.sh deploy
#   ./scripts/logtail.sh reset
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

case "${1:-}" in
    deploy)
        $K apply -f k8s/logtail.yaml || exit 2
        $K rollout status daemonset/logtail --timeout=180s || exit 2
        ;;
    reset)
        # Through the shelf container, which is the only thing that mounts the directory with a
        # shell. `kubectl exec daemonset/…` resolves to the DaemonSet's one pod on this one-node
        # cluster; a missing DaemonSet is a kubectl error and the exit 2 below.
        $K exec daemonset/logtail -c shelf -- find /data/podlogs -mindepth 1 -delete || {
            echo "logtail: could not clear /data/podlogs -- is the tailer deployed? (./scripts/logtail.sh deploy)" >&2
            exit 2
        }
        $K delete pod -l app=logtail --wait=true >/dev/null || exit 2
        $K rollout status daemonset/logtail --timeout=180s || exit 2
        echo "every run directory cleared; the tailer is re-reading every log the kubelet still has"
        ;;
    *)
        sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
        exit 2
        ;;
esac
