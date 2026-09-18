#!/usr/bin/env bash
# How long is the cluster leaderless after its leader dies ungracefully?
#
# This is the experiment the RavenDB arm exists for, because the two backends cannot give the
# same answer:
#
#   PostgreSQL — leadership is a session-scoped advisory lock. The backend dies with the pod and
#   the server releases the lock immediately. A survivor can win the next election on its next
#   heartbeat, so the leaderless window is bounded by the control-plane cadence (seconds).
#
#   RavenDB — leadership is a compare-exchange document carrying ExpirationTime, and nothing in
#   the server clears it. RavenDbMessageStore.Locking.TryAttainLeadershipLockAsync gives a peer
#   two routes and both are closed while the dead leader's value is unexpired:
#     * PutCompareExchangeValueOperation(key, newLock, index: 0) means "create only if absent",
#       and the dead leader's value is still there, so it fails;
#     * tryTakeOverIfExpiredAsync returns false while ExpirationTime > UtcNow.
#   Every heartbeat rewrites ExpirationTime to UtcNow.AddMinutes(5), so at the moment of death
#   the lock has most of five minutes left. PREDICTION: a leaderless window of ~5 minutes, with
#   nothing logged and no server-side actor able to shorten it.
#
# THE NEMESIS HAS TO BE UNGRACEFUL, AND `kubectl delete --force --grace-period=0` IS NOT.
#
# A graceful shutdown calls ReleaseLeadershipLockAsync, which DELETES the compare-exchange value.
# That hands leadership over cleanly and skips the entire expiry mechanism -- it is the path a
# rolling deploy takes, and it is not the one under test. What is under test is a leader that dies
# without running shutdown at all: OOM kill, node loss, SIGKILL past the grace period.
#
# The first version of this script used `kubectl delete pod --force --grace-period=0` and measured
# a 6-second failover. That number was real and meant nothing: --force removes the API object
# immediately but the kubelet still delivers SIGTERM, .NET still ran its shutdown, and the lock was
# released rather than abandoned. The giveaway was in the data -- the lock read `none` one second
# after the kill, and a NodeStopped record appeared, which only the graceful path writes.
#
# `kubectl exec -- kill -9 1` does not work either: the kernel will not deliver an unhandled signal
# to PID 1 from inside its own PID namespace. So the kill has to come from the host side, via
# crictl on the minikube node.
#
# MODE=graceful reproduces the old behaviour on purpose, because "how fast is a clean handover"
# is a worthwhile control to measure against.
#
#   ./scripts/leader-kill.sh [watch-seconds]                  # default 420 (past the 5 min expiry)
#   MODE=graceful ./scripts/leader-kill.sh 120                # the control arm
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"
source scripts/backend.sh

TOOLS=".tools/safetylab"
# Same requirement monitor.sh states: the host-side binary needs the .NET runtime, which this repo
# provides through `nix develop`. Say so plainly -- the raw failure is a wall of apphost text about
# DOTNET_ROOT that says nothing about what to do.
command -v dotnet >/dev/null || {
    echo "no dotnet on PATH -- run this inside 'nix develop'." >&2
    exit 2
}
if [ ! -x "$TOOLS/safetylab" ]; then
    echo "leader-kill.sh needs the safetylab binary: ./scripts/monitor.sh deploy (or dotnet publish" >&2
    echo "    src/SafetyLab -c Release -o $TOOLS) -- it owns the pid resolution and its guards." >&2
    exit 2
fi

WATCH="${1:-420}"
BACKEND=$(sim_backend)
OUT="${OUT:-runs/leader-kill-$BACKEND}"
mkdir -p "$OUT"
TSV="$OUT/timeline.tsv"

# The lock columns only exist on RavenDB; on PostgreSQL they stay '-' rather than being dropped,
# so one parser reads both arms' files.
printf 'elapsed_s\tleader_pod\tleader_node\tlock_holder\tlock_expires_in_s\tplaced\n' > "$TSV"

leader_row() {
    case "$BACKEND" in
        ravendb) _raven query leader 2>/dev/null ;;
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

# NodeStopped is written by the NodeStopped() observer callback, which runs only on the graceful
# shutdown path -- a SIGKILLed process cannot write it. Counting them before and after the kill is
# therefore a direct assertion that the nemesis did what it claims, and it is the check that caught
# the --force --grace-period=0 mistake.
stopped_count() {
    case "$BACKEND" in
        ravendb) _raven query per-minute --event NodeStopped 2>/dev/null | awk -F'\t' '{s+=$2} END {print s+0}' ;;
        *) _psql "select count(*) from wolverine.wolverine_node_records where event_name = 'NodeStopped';" \
               | tr -d '[:space:]' ;;
    esac
}

STOPPED_BEFORE=$(stopped_count)

echo
MODE="${MODE:-sigkill}"
START=$(date +%s)

if [ "$MODE" = "graceful" ]; then
    echo "== control arm: ordinary pod delete (shutdown hooks DO run) =="
    $K delete pod "$VICTIM" --wait=false 2>&1 | sed 's/^/  /'
else
    # SIGKILL the container's host-side process, from the node, outside its PID namespace.
    echo "== SIGKILL from the node (no SIGTERM, no shutdown hooks) =="
    # Resolving a container to a killable host pid is done in C# (safetylab host-pid), not here.
    # The shell version of this block grepped the first `"pid"` out of `crictl inspect` -- which is
    # a namespace descriptor reading 1 -- and ran `kill -9 1` against the minikube node's init. It
    # survived only because PID 1 ignores unhandled signals from inside its own namespace, and the
    # run then reported a failover for a kill that never happened.
    #
    # `safetylab host-pid` parses .info.pid as JSON and refuses to print anything unless the pid is
    # > 1 AND /proc/<pid>/cmdline contains ChurnSim. Both rules are unit-tested against a captured
    # crictl document that still has the misleading "pid": 1 ahead of the real one
    # (tests/SafetyLab.Tests). If it exits non-zero, nothing gets killed.
    HOSTPID=$("$TOOLS/safetylab" host-pid --pod "$VICTIM" --container churnsim --expect ChurnSim) || exit 2

    echo "  $VICTIM -> host pid $HOSTPID"

    # DRY_RUN resolves the whole target -- leader, pod, container, validated host pid -- and stops
    # before the signal. A fault injector ought to be able to show what it would destroy, and it
    # makes the resolution path testable against a live cluster without spending a five-minute
    # outage to find out the plumbing works.
    if [ -n "${DRY_RUN:-}" ]; then
        echo
        echo "DRY_RUN set -- would kill host pid $HOSTPID in $VICTIM. Nothing was signalled."
        exit 0
    fi

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

    # A NEW leader means a live node that is not the one just killed.
    #
    # `leader_row` answers the single word "none" when there is no leader, and `cut -f2` on a line
    # with no tab returns that whole word -- so "none" has to be excluded explicitly. Without it
    # this fired at t+0 on the leaderless state and reported the outage as an instant recovery,
    # which is the exact inversion of the thing being measured.
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

# The verdict is worthless unless the nemesis actually fired. A NodeStopped record appearing across
# a run that claims to be a SIGKILL means the victim ran its shutdown, released the lock, and the
# expiry path was never reached -- so the window above is a graceful-handover measurement wearing
# the wrong label.
echo "  NodeStopped        : $STOPPED_BEFORE -> $STOPPED_AFTER"
if [ "$MODE" != "graceful" ] && [ "${STOPPED_AFTER:-0}" -gt "${STOPPED_BEFORE:-0}" ]; then
    echo
    echo "  *** INVALID RUN ***  a NodeStopped record was written, so the victim shut down"
    echo "  gracefully and released its lock. This did NOT exercise the expiry path; the window"
    echo "  above is a clean-handover time. Do not report it as a failover measurement."
fi
echo "  timeline           : $TSV"
