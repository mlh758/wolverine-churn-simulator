#!/usr/bin/env bash
# When a NON-leader dies ungracefully, does the surviving leader re-place its agents?
#
# DEPENDS ON  a deployed churnsim cluster (either arm), $SAFETYLAB, minikube ssh + crictl on the
#             node. The RavenDB arm additionally needs ./scripts/monitor.sh deploy.
# REQUIRES    a settled cluster with a leader that resolves to a live pod, and at least one live
#             pod that is not the leader. Both are refusals.
# PRODUCES    $OUT/timeline.tsv, one row per 5s poll, and a verdict on stdout. MUTATES the cluster:
#             it kills a pod. Prints *** INVALID RUN *** if a NodeStopped record appeared, which
#             means the victim shut down gracefully and the ungraceful path was never exercised.
#
# WHY THIS EXISTS, SEPARATELY FROM leader-kill.sh. leader-kill.sh answers "how long is the cluster
# leaderless", and on RavenDB it never reaches a rebalance at all: nobody leads for the ~300 s
# compare-exchange expiry, and then the victim's own restarted instance reclaims leadership, so the
# agent population never moves. Killing a FOLLOWER keeps the leader alive and untouched throughout,
# which isolates the rebalance: one node dies, the leader must redistribute its agents onto the
# survivors, and nothing about leadership changes. That is the nemesis that reproduces the
# missing-agent shortfall on BOTH backends (2026-09-19 entry in RESULTS.md) -- ~41/500 assignments
# are never written, with assigned == running throughout, so no checker and no Wolverine health
# signal reports it.
#
# THE NEMESIS HAS TO BE UNGRACEFUL, for the same reason as leader-kill.sh: `kubectl delete pod
# --force --grace-period=0` still delivers SIGTERM and Wolverine runs its shutdown, which releases
# the agents cleanly and does NOT reproduce this. The kill comes from the host side via crictl.
#
# ARGUMENTS
#
#   ./scripts/follower-kill.sh [watch-seconds]   how long to watch; default 420, matching
#                                                leader-kill.sh so the two are comparable.
#   DRY_RUN=1 ./scripts/follower-kill.sh         resolve the spared leader, the victim and a
#                                                validated host pid, print them, and stop before
#                                                signalling.
#   OUT=<dir> ./scripts/follower-kill.sh         where timeline.tsv goes; default
#                                                runs/follower-kill-<backend>.
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"
source scripts/backend.sh

# $SAFETYLAB owns the pid resolution and its guards, so it is not optional here.
require_safetylab

WATCH="${1:-420}"
BACKEND=$(sim_backend)
OUT="${OUT:-runs/follower-kill-$BACKEND}"
mkdir -p "$OUT"
TSV="$OUT/timeline.tsv"
printf 'elapsed_s\tleader_pod\tvictim_restarts\tplaced\n' > "$TSV"

leader_row() {
    case "$BACKEND" in
        ravendb) _raven query leader 2>/dev/null ;;
        *) _psql "select coalesce(n.description, 'unknown') || chr(9) || a.node_id
                    from wolverine.wolverine_node_assignments a
                    left join wolverine.wolverine_nodes n on n.id = a.node_id
                   where a.id like 'wolverine://leader%';" ;;
    esac
}

# NodeStopped is written only on the graceful shutdown path -- a SIGKILLed process cannot write one.
# Counting before and after is a direct assertion that the nemesis did what it claims.
stopped_count() {
    case "$BACKEND" in
        ravendb) _raven query per-minute --event NodeStopped 2>/dev/null | awk -F'\t' '{s+=$2} END {print s+0}' ;;
        *) _psql "select count(*) from wolverine.wolverine_node_records where event_name = 'NodeStopped';" \
               | tr -d '[:space:]' ;;
    esac
}

echo "== follower-kill on $BACKEND =="

# Refuse to measure a cluster that is not settled. A rebalance timed from a cluster that was
# already mid-election is not a rebalance measurement.
PLACED_BEFORE=$(db_placed)
before=$(leader_row)
if [ -z "$before" ] || [ "$before" = "none" ]; then
    echo "no leader -- the cluster is not settled. Wait and retry." >&2
    exit 2
fi
LEADER_POD=$(cut -f1 <<<"$before")
echo "leader (SPARED): $LEADER_POD"
echo "placed:          $PLACED_BEFORE sim agents"

# A follower is any live churnsim pod that is not the leader. Phase-filtered: a Terminating pod
# from an earlier rollout is not a victim, and killing one measures nothing.
VICTIM=""
for p in $($K get pods -l app=churnsim --field-selector=status.phase=Running \
             -o jsonpath='{range .items[*]}{.metadata.name}{"\n"}{end}'); do
    if [ "$p" != "$LEADER_POD" ]; then VICTIM="$p"; break; fi
done
if [ -z "$VICTIM" ]; then
    echo "no live non-leader pod found -- refusing to guess." >&2
    exit 2
fi
echo "victim:          $VICTIM"

STOPPED_BEFORE=$(stopped_count)

echo
echo "== SIGKILL from the node (no SIGTERM, no shutdown hooks) =="
# `safetylab host-pid` parses crictl's .info.pid as JSON and refuses to print anything unless the
# pid is > 1 AND /proc/<pid>/cmdline contains ChurnSim. If it exits non-zero, nothing gets killed --
# which is why the kill target is resolved there and not with a grep here.
HOSTPID=$("$SAFETYLAB" host-pid --pod "$VICTIM" --container churnsim --expect ChurnSim) || exit 2
echo "  $VICTIM -> host pid $HOSTPID"

if [ -n "${DRY_RUN:-}" ]; then
    echo
    echo "DRY_RUN set -- would kill host pid $HOSTPID in $VICTIM. Nothing was signalled."
    exit 0
fi

START=$(date +%s)
minikube ssh -- "sudo kill -9 $HOSTPID" 2>&1 | sed 's/^/  /'

while :; do
    now=$(( $(date +%s) - START ))
    [ "$now" -ge "$WATCH" ] && break

    row=$(leader_row)
    pod=$(cut -f1 <<<"$row")
    restarts=$($K get pod "$VICTIM" -o jsonpath='{.status.containerStatuses[0].restartCount}' 2>/dev/null)
    n=$(db_placed)

    printf '%s\t%s\t%s\t%s\n' "$now" "${pod:-none}" "${restarts:--}" "${n:--}" >> "$TSV"
    sleep 5
done

PLACED_AFTER=$(db_placed)
STOPPED_AFTER=$(stopped_count)

echo
echo "======== RESULT ========"
echo "  backend            : $BACKEND"
echo "  leader (spared)    : $LEADER_POD"
echo "  victim             : $VICTIM"
echo "  placed before      : $PLACED_BEFORE"
echo "  placed after       : $PLACED_AFTER"
if [ "${PLACED_AFTER:-0}" -lt "${PLACED_BEFORE:-0}" ]; then
    echo "  SHORTFALL          : $(( PLACED_BEFORE - PLACED_AFTER )) agents never re-placed"
fi
echo "  NodeStopped        : $STOPPED_BEFORE -> $STOPPED_AFTER"
if [ "${STOPPED_AFTER:-0}" -gt "${STOPPED_BEFORE:-0}" ]; then
    echo
    echo "  *** INVALID RUN ***  a NodeStopped record was written, so the victim shut down"
    echo "  gracefully and released its agents cleanly. This did NOT exercise the ungraceful"
    echo "  path; the numbers above are a clean-handover measurement."
fi
echo "  timeline           : $TSV"
