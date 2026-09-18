#!/usr/bin/env bash
# Capture a SafetyLab run: the monitor's server-side samples plus every churnsim pod's log,
# into runs/<name>/, then check it.
#
#   ./scripts/monitor.sh deploy            # build + deploy the in-cluster monitor (once)
#   ./scripts/monitor.sh start rollout-1   # begin capturing into runs/rollout-1
#   ./scripts/monitor.sh mark rollout-end  # timestamp a phase boundary
#   ./scripts/monitor.sh stop              # end the capture
#   ./scripts/monitor.sh check [name]      # run the checkers over a captured run
#
# Pod logs are followed per-pod and started as pods appear, because `kubectl logs` cannot
# reach a pod once it is gone -- and during a rolling deploy the pods that matter most are
# exactly the ones that disappear. A pod replaced while nothing was following it contributes
# no agent residencies at all, which silently weakens S5/S6/S7. Start the capture BEFORE the
# rollout, and read the coverage check afterwards.
set -uo pipefail
cd "$(dirname "$0")/.."

export PATH="$HOME/.local/bin:$PATH"
KUBECTL="minikube kubectl -- --context=minikube"

RUNS="runs"
ACTIVE="$RUNS/.active"
TOOLS=".tools/safetylab"

# sim_backend(), so `deploy` picks the manifest matching the arm under test.
source scripts/backend.sh

now() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }

ensure_tool() {
    # Rebuild when any source is newer than the binary. A plain existence check silently ran a
    # stale checker after every source edit -- including, once, re-reporting a run against the
    # very bug that had just been fixed.
    if [ -x "$TOOLS/safetylab" ] && [ -z "$(find src/SafetyLab -name '*.cs' -newer "$TOOLS/safetylab" -print -quit)" ]; then
        return 0
    fi

    command -v dotnet >/dev/null || {
        echo "no dotnet on PATH -- run this inside 'nix develop'" >&2
        exit 2
    }
    echo "== building safetylab ==" >&2
    dotnet publish src/SafetyLab -c Release -o "$TOOLS" -v q --nologo >&2 || exit 2
}

safetylab() {
    ensure_tool
    "$TOOLS/safetylab" "$@"
}

active_run() {
    [ -f "$ACTIVE" ] || { echo "no active run -- ./scripts/monitor.sh start <name>" >&2; exit 2; }
    cat "$ACTIVE"
}

cmd_deploy() {
    echo "== building the safetylab image =="
    podman build -f Dockerfile.safetylab -t localhost/safetylab:local .
    podman save localhost/safetylab:local -o /tmp/safetylab-local.tar
    minikube image load /tmp/safetylab-local.tar
    rm -f /tmp/safetylab-local.tar

    # One image, two manifests. The monitor binary speaks both stores; which one it watches is
    # an argument, and it must match the arm that is deployed -- a monitor pointed at an empty
    # Postgres while the cluster runs on RavenDB would produce a history full of nothing and
    # every safety check would pass over it.
    local backend manifest
    backend=$(sim_backend)
    case "$backend" in
        ravendb) manifest="k8s/safetylab-ravendb.yaml" ;;
        *) manifest="k8s/safetylab.yaml" ;;
    esac

    echo "== deploying the monitor ($backend) =="
    $KUBECTL apply -f "$manifest"

    # `apply` reports "unchanged" when only the image CONTENTS moved -- the tag is always
    # localhost/safetylab:local -- so the running pod keeps the old binary and every measurement
    # afterwards silently comes from the previous build. Same class as the .tools/safetylab
    # existence-cache trap: it does not fail, it just answers with yesterday's code. Restart
    # unconditionally; the pod is cheap and a stale monitor is not.
    $KUBECTL rollout restart deployment/safetylab
    $KUBECTL rollout status deployment/safetylab --timeout=180s
}

cmd_start() {
    local name="${1:-run-$(date +%s)}"
    local dir="$RUNS/$name"

    if [ -f "$ACTIVE" ]; then
        echo "a capture is already running ($(cat "$ACTIVE")) -- stop it first" >&2
        exit 2
    fi

    ensure_tool
    mkdir -p "$dir"
    if [ -s "$dir/history.jsonl" ]; then
        echo "refusing to overwrite a captured run in $dir -- pick another name" >&2
        exit 2
    fi

    : > "$dir/marks.jsonl"
    echo "$name" > "$ACTIVE"
    mkdir -p "$dir/.pids"

    # The monitor pod is long-lived and was probably started before this capture, so take only
    # what it emits from here on. --since=1s beats -f from the beginning: an old pod's backlog
    # would land in this run's history with timestamps that predate it.
    local pod
    pod=$($KUBECTL get pod -l app=safetylab -o jsonpath='{.items[0].metadata.name}')
    [ -n "$pod" ] || { echo "no safetylab pod -- run './scripts/monitor.sh deploy' first" >&2; exit 2; }

    # setsid + disown, not a bare `&`. These outlive `start` on purpose, and the follower loop
    # never exits on its own -- so under any caller that waits for its children (`bash -c`, a
    # script, CI) a bare background job makes `start` hang forever instead of returning. Detaching
    # them into their own session also means a Ctrl-C aimed at this script cannot take the capture
    # down mid-rollout.
    setsid $KUBECTL logs -f --since=1s "$pod" >> "$dir/history.jsonl" 2>/dev/null &
    echo $! > "$dir/.pids/monitor"
    disown 2>/dev/null || true

    # Follow churnsim pods, picking up new ones as a rollout creates them.
    setsid bash "$0" __followers "$dir" >/dev/null 2>&1 &
    echo $! > "$dir/.pids/followers"
    disown 2>/dev/null || true

    echo "capturing into $dir (monitor pod $pod)"
    echo "  ./scripts/monitor.sh mark <label>   to timestamp a phase"
    echo "  ./scripts/monitor.sh stop           when the run is done"
}

followers_loop() {
    local dir="$1"
    local followed=" "

    while :; do
        for pod in $($KUBECTL get pods -l app=churnsim -o jsonpath='{.items[*].metadata.name}' 2>/dev/null); do
            case "$followed" in
                *" $pod "*) continue ;;
            esac
            followed="$followed$pod "

            # Whole log, not --since: a pod's SIM-IDENTITY line is written at startup and the
            # identity map is what makes S3/S4/S6/S7 possible at all.
            #
            # Retry rather than attach once. A pod shows up in `get pods` while still
            # ContainerCreating, and `kubectl logs -f` against it fails immediately -- so a
            # single attempt marks the pod as followed, captures nothing, and leaves that node
            # invisible to every pod-side check. That is not hypothetical: it produced a false
            # S4 ("leader lock held by an address that never announced itself") on a healthy
            # cluster, because the pod holding leadership was one the capture never attached to.
            # Loop until the pod is actually gone.
            ( while $KUBECTL get pod "$pod" >/dev/null 2>&1; do
                  $KUBECTL logs -f --timestamps "$pod" 2>/dev/null \
                      | "$TOOLS/safetylab" harvest --pod "$pod" >> "$dir/pods.$pod.jsonl"
                  sleep 2
              done ) &
            echo $! >> "$dir/.pids/pods"
        done
        sleep 2
    done
}

cmd_mark() {
    local label="${1:?usage: monitor.sh mark <label>}"
    local dir="$RUNS/$(active_run)"
    printf '{"kind":"mark","ts":"%s","label":"%s"}\n' "$(now)" "$label" >> "$dir/marks.jsonl"
    echo "marked '$label' in $dir"
}

cmd_stop() {
    local name dir
    name=$(active_run)
    dir="$RUNS/$name"

    # Give the followers a moment to drain whatever the pods logged last.
    sleep 3

    if [ -d "$dir/.pids" ]; then
        # Kill process GROUPS, not pids. `start` detaches with setsid, so each recorded pid is a
        # group leader with children (kubectl | safetylab harvest) that a plain `kill` leaves
        # running -- and a leaked follower keeps appending the NEXT run's pods into this run's
        # directory, which is how a finished capture silently grew for another twenty minutes and
        # picked up a different experiment's pods.
        while read -r pid; do
            [ -n "$pid" ] || continue
            kill -- "-$pid" 2>/dev/null || kill "$pid" 2>/dev/null
        done < <(cat "$dir/.pids"/* 2>/dev/null)

        sleep 1

        # Anything still standing after the group kill.
        while read -r pid; do
            [ -n "$pid" ] || continue
            kill -9 -- "-$pid" 2>/dev/null || kill -9 "$pid" 2>/dev/null
        done < <(cat "$dir/.pids"/* 2>/dev/null)

        rm -rf "$dir/.pids"
    fi

    # Prove it: a stop that did not stop is worse than no stop at all, because the run directory
    # keeps changing under the checker.
    local leaked
    leaked=$(ps ax -o args= 2>/dev/null | grep -c "[s]afetylab harvest" || true)
    if [ "${leaked:-0}" -gt 0 ]; then
        echo "WARNING: $leaked harvest process(es) survived the stop; run directory may keep growing" >&2
    fi

    rm -f "$ACTIVE"
    echo "stopped; captured $(wc -l < "$dir/history.jsonl" 2>/dev/null || echo 0) samples into $dir"
    echo "  ./scripts/monitor.sh check $name"
}

cmd_check() {
    local name="${1:-}"
    [ -n "$name" ] || name=$(active_run)
    shift 2>/dev/null || true
    safetylab check "$RUNS/$name" "$@"
}

case "${1:-}" in
    # Internal: the detached follower loop re-enters the script here.
    __followers) shift; followers_loop "$@" ;;
    deploy) shift; cmd_deploy "$@" ;;
    start)  shift; cmd_start "$@" ;;
    mark)   shift; cmd_mark "$@" ;;
    stop)   shift; cmd_stop "$@" ;;
    check)  shift; cmd_check "$@" ;;
    *)
        sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'
        exit 2
        ;;
esac
