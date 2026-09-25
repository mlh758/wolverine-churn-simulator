#!/usr/bin/env bash
# How long is the cluster leaderless after its leader dies ungracefully?
#
# DEPENDS ON  a deployed churnsim cluster (any arm), $SAFETYLAB, minikube ssh + crictl on the
#             node. The RavenDB arm additionally needs ./scripts/monitor.sh deploy.
# REQUIRES    a settled cluster with a leader that resolves to a live pod; both are refusals. A
#             window timed from a cluster already mid-election is not a failover measurement.
# PRODUCES    $OUT/timeline.tsv, one row per 5s poll; $OUT/post/raw.<pod>.<attempt>.jsonl, every
#             container log off the node tailer, the victim's pre-kill one included; and a verdict on
#             stdout. MUTATES the cluster:
#             it kills a pod. Prints *** INVALID RUN *** if a NodeStopped record appeared, which
#             means the victim shut down gracefully and the expiry path was never exercised.
#
# The two backends cannot give the same answer -- PostgreSQL leadership is a session-scoped
# advisory lock the server releases when the pod dies, RavenDB's is a compare-exchange document
# whose ExpirationTime nothing clears. See the 2026-09-18 entry in RESULTS.md.
#
# THE NEMESIS HAS TO BE UNGRACEFUL, AND `kubectl delete --force --grace-period=0` IS NOT: --force
# removes the API object immediately, but the kubelet still delivers SIGTERM and .NET still runs
# its shutdown, which releases the lock rather than abandoning it. `kubectl exec -- kill -9 1` is
# also a no-op -- the kernel will not deliver an unhandled signal to PID 1 from inside its own PID
# namespace. So the kill comes from the host side, via crictl on the minikube node.
#
# ARGUMENTS
#
#   ./scripts/leader-kill.sh [watch-seconds]     how long to watch; default 420, past the 5 min
#                                                RavenDB lock expiry. Shorter on PostgreSQL is fine.
#   MODE=graceful ./scripts/leader-kill.sh 120   the control arm: an ordinary pod delete, so the
#                                                shutdown hooks DO run. "How fast is a clean
#                                                handover" is worth measuring against.
#   DRY_RUN=1 ./scripts/leader-kill.sh           resolve leader, pod, container and validated host
#                                                pid, print the target, and stop before signalling.
#   OUT=<dir> ./scripts/leader-kill.sh           where timeline.tsv goes; default
#                                                runs/leader-kill-<backend>.
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"
source scripts/backend.sh

# $SAFETYLAB owns the pid resolution and its guards, so it is not optional here.
require_safetylab

WATCH="${1:-420}"
BACKEND=$(sim_backend)
OUT="${OUT:-runs/leader-kill-$BACKEND}"
mkdir -p "$OUT"
TSV="$OUT/timeline.tsv"

# The lock columns only exist on RavenDB; on the RDBMS arms they stay '-' rather than being
# dropped, so one parser reads every arm's files.
printf 'elapsed_s\tleader_pod\tleader_node\tlock_holder\tlock_expires_in_s\tplaced\n' > "$TSV"

leader_row() {
    case "$BACKEND" in
        ravendb) _raven query leader 2>/dev/null ;;
        mysql) _mysql "select coalesce(n.description, 'unknown'), a.node_id
                    from wolverine.wolverine_node_assignments a
                    left join wolverine.wolverine_nodes n on n.id = a.node_id
                   where a.id like 'wolverine://leader%';" ;;
        *) _psql "select coalesce(n.description, 'unknown') || chr(9) || a.node_id
                    from wolverine.wolverine_node_assignments a
                    left join wolverine.wolverine_nodes n on n.id = a.node_id
                   where a.id like 'wolverine://leader%';" ;;
    esac
}

lock_row() {
    case "$BACKEND" in
        ravendb) _raven query lock 2>/dev/null | head -1 ;;
        *) printf -- '-\t-\t-\t-\n' ;;
    esac
}

echo "== leader-kill on $BACKEND =="

# Refuse to measure a cluster that is not settled: a leaderless window timed from a cluster that
# was already mid-election is not a failover measurement.
placed=$(db_placed)
before=$(leader_row)
if [ -z "$before" ] || [ "$before" = "none" ]; then
    echo "no leader to kill -- the cluster is not settled. Wait and retry." >&2
    exit 2
fi

VICTIM=$(cut -f1 <<<"$before")
VICTIM_NODE=$(cut -f2 <<<"$before")
echo "leader:  $VICTIM  ($VICTIM_NODE)"
echo "placed:  $placed sim agents"
[ "$BACKEND" = "ravendb" ] && echo "lock:    $(lock_row)"

if ! $K get pod "$VICTIM" >/dev/null 2>&1; then
    echo "leader pod '$VICTIM' is not a live pod -- stale registration? Refusing to guess." >&2
    exit 2
fi

# NodeStopped is written only on the graceful shutdown path -- a SIGKILLed process cannot write it.
# Counting before and after is therefore a direct assertion that the nemesis did what it claims.
stopped_count() {
    case "$BACKEND" in
        ravendb) _raven query per-minute --event NodeStopped 2>/dev/null | awk -F'\t' '{s+=$2} END {print s+0}' ;;
        mysql) _mysql "select count(*) from wolverine.wolverine_node_records where event_name = 'NodeStopped';" \
               | tr -d '[:space:]' ;;
        *) _psql "select count(*) from wolverine.wolverine_node_records where event_name = 'NodeStopped';" \
               | tr -d '[:space:]' ;;
    esac
}

STOPPED_BEFORE=$(stopped_count)

echo
MODE="${MODE:-sigkill}"

# The tailer run this kill's logs go to (see capture-logs.sh). Inside an active SafetyLab capture
# the capture owns the run and rolling the tailer would split it, so it is only started here when
# there is none. START is taken after, right before the signal: the roll takes seconds and they
# are not part of the leaderless window.
start_podlogs_run() {
    if [ -f runs/.active ]; then
        echo "  (a capture is active; its tailer run collects this kill)"
    else
        "$SAFETYLAB" podlogs start "$(basename "$OUT")-$(date -u +%Y%m%dT%H%M%SZ)" || exit 2
    fi
}

if [ "$MODE" = "graceful" ]; then
    echo "== control arm: ordinary pod delete (shutdown hooks DO run) =="
    start_podlogs_run
    START=$(date +%s)
    $K delete pod "$VICTIM" --wait=false 2>&1 | sed 's/^/  /'
else
    # SIGKILL the container's host-side process, from the node, outside its PID namespace.
    echo "== SIGKILL from the node (no SIGTERM, no shutdown hooks) =="
    # `safetylab host-pid` parses crictl's .info.pid as JSON and refuses to print anything unless
    # the pid is > 1 AND /proc/<pid>/cmdline contains ChurnSim. If it exits non-zero, nothing gets
    # killed -- which is why the kill target is resolved there and not with a grep here.
    HOSTPID=$("$SAFETYLAB" host-pid --pod "$VICTIM" --container churnsim --expect ChurnSim) || exit 2

    echo "  $VICTIM -> host pid $HOSTPID"

    # DRY_RUN stops here: a fault injector ought to be able to show what it would destroy, and it
    # makes the resolution path testable without spending a five-minute outage on plumbing.
    if [ -n "${DRY_RUN:-}" ]; then
        echo
        echo "DRY_RUN set -- would kill host pid $HOSTPID in $VICTIM. Nothing was signalled."
        exit 0
    fi

    start_podlogs_run
    START=$(date +%s)
    minikube ssh -- "sudo kill -9 $HOSTPID" 2>&1 | sed 's/^/  /'
fi

FIRST_NEW=""
while :; do
    now=$(( $(date +%s) - START ))
    [ "$now" -ge "$WATCH" ] && break

    row=$(leader_row)
    pod=$(cut -f1 <<<"$row"); node=$(cut -f2 <<<"$row")
    lock=$(lock_row)
    holder=$(cut -f2 <<<"$lock"); ttl=$(cut -f4 <<<"$lock")
    n=$(db_placed)

    printf '%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$now" "${pod:-none}" "${node:--}" "${holder:--}" "${ttl:--}" "${n:--}" >> "$TSV"

    # A NEW leader means a live node that is not the one just killed. "none" must be excluded
    # explicitly: `leader_row` answers that single word when there is no leader, and `cut -f2` on a
    # line with no tab returns the whole word -- which reads as an instant recovery.
    if [ -n "$node" ] && [ "$node" != "-" ] && [ "$node" != "none" ] \
       && [ "$node" != "$VICTIM_NODE" ] && [ -z "$FIRST_NEW" ]; then
        FIRST_NEW="$now"
        echo "  t+${now}s  NEW LEADER: $pod"
    fi

    sleep 5
done

STOPPED_AFTER=$(stopped_count)

echo
echo "======== RESULT ========"
echo "  backend            : $BACKEND"
echo "  mode               : $MODE"
echo "  killed             : $VICTIM"
if [ -n "$FIRST_NEW" ]; then
    echo "  leaderless window  : ${FIRST_NEW}s"
else
    echo "  leaderless window  : STILL LEADERLESS after ${WATCH}s"
fi

# The verdict is worthless unless the nemesis actually fired: a NodeStopped record across a run
# claiming to be a SIGKILL means the expiry path was never reached.
echo "  NodeStopped        : $STOPPED_BEFORE -> $STOPPED_AFTER"
if [ "$MODE" != "graceful" ] && [ "${STOPPED_AFTER:-0}" -gt "${STOPPED_BEFORE:-0}" ]; then
    echo
    echo "  *** INVALID RUN ***  a NodeStopped record was written, so the victim shut down"
    echo "  gracefully and released its lock. This did NOT exercise the expiry path; the window"
    echo "  above is a clean-handover time. Do not report it as a failover measurement."
fi
echo "  timeline           : $TSV"

# The victim's pre-kill log is the lower attempt (capture-logs.sh).
echo
./scripts/capture-logs.sh "$OUT/post"
