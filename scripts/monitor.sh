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

now() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }

ensure_tool() {
    [ -x "$TOOLS/safetylab" ] && return 0
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

    echo "== deploying the monitor =="
    $KUBECTL apply -f k8s/safetylab.yaml
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

    $KUBECTL logs -f --since=1s "$pod" >> "$dir/history.jsonl" 2>/dev/null &
    echo $! > "$dir/.pids/monitor"

    # Follow churnsim pods, picking up new ones as a rollout creates them.
    followers_loop "$dir" &
    echo $! > "$dir/.pids/followers"

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
            ( $KUBECTL logs -f "$pod" 2>/dev/null \
                | "$TOOLS/safetylab" harvest --pod "$pod" >> "$dir/pods.$pod.jsonl" ) &
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
        while read -r pid; do kill "$pid" 2>/dev/null; done < <(cat "$dir/.pids"/* 2>/dev/null)
        # The followers loop spawns children of its own; take the group down with it.
        pkill -P "$(cat "$dir/.pids/followers" 2>/dev/null)" 2>/dev/null
        rm -rf "$dir/.pids"
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
