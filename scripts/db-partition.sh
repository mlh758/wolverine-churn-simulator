#!/usr/bin/env bash
# E9 — cut the LEADER off from PostgreSQL and watch what its lock does.
#
# DEPENDS ON  a deployed PostgreSQL churnsim cluster, ./scripts/monitor.sh deploy, $SAFETYLAB,
#             minikube ssh with iptables-nft on the node.
# REQUIRES    the PostgreSQL arm, a settled cluster, a leader whose advisory lock is held from the
#             leader pod's own address, no partition already armed, no capture already active.
# PRODUCES    runs/<name>/ with the monitor's history, every pod's log, phase marks, timeline.tsv
#             (one row per 5s poll), post/ (raw pod logs), overlaps.txt and report.txt. MUTATES
#             the cluster: it writes DROP rules into the node's FORWARD chain, and removes them
#             from a trap.
#
# THE INVERSE OF lock-kill.sh, AND THAT IS THE WHOLE POINT. There the server terminated the lock
# session: the lock died with it, and the client found out immediately because the connection
# broke visibly -- 6.39.0 logs `Lost advisory-lock connection ... clearing held lock ids`, steps
# down and calls an election (RESULTS.md 2026-09-19). Here nothing is terminated. The session
# stays alive on the server, holding the advisory lock, while the CLIENT cannot reach it. So:
#
#   * the lock is still held, so no peer can take it;
#   * the node is still registered, so nothing ejects it -- and the actor that ejects stale nodes
#     is the leader, which is the node that is cut off;
#   * the assignment table is frozen mid-health and reads as fully placed.
#
# That is a stall that nothing in the store shows, which is why S11 exists. How long it can last
# is bounded by whatever eventually reaps the server-side session -- PostgreSQL's
# `tcp_keepalives_idle` defaults to 0, meaning the system default, typically 7200s. A short hold
# answers "does it strand at all"; finding the bound needs a long one.
#
# WHY THE OBSERVER IS UNAFFECTED. Only the leader's pod is cut, and only from the pg pod. The
# monitor reads PostgreSQL from its own pod, and every psql here runs inside the pg pod, so both
# sit on the database side of the cut throughout -- which is the side that can still see the lock.
#
# THE BOUNCE ARM (BOUNCE=...). Cutting the leader off is a stall, and a stall on a CP-flavoured
# system is defensible behaviour rather than a bug. What is NOT obvious is what the cluster does
# when the partition heals after the roster moved underneath the isolated leader. So this arm
# restarts a NON-isolated peer at the midpoint of the hold, while nothing can redistribute:
#
#   * the peer shuts down gracefully and deletes its node row;
#   * `wolverine_node_assignments.node_id` is ON DELETE CASCADE, so its assignment rows go with it;
#   * its agents are therefore unassigned AND not running, and the only actor that could place
#     them again is the leader, which cannot reach the database. `placed` drops and stays down.
#
# Then the cut heals onto a leader whose in-memory view of the roster is minutes stale, with a
# replacement pod it has never seen and a third of the agents owned by nobody. The questions are
# the post-heal ones: does it converge, does it duplicate, and for how long.
#
# WHICH INSTRUMENT ANSWERS THE DUPLICATE QUESTION. Not `safetylab overlaps` alone: that reads
# raw pod logs from `post/`, which only contains pods that are still alive at the end, and the
# bounced pod is by definition gone. **S5** is the one that covers it -- it builds residencies
# from the capture's own harvested AGENT-START/STOP stream, which the monitor's follower collected
# from the bounced pod while it was still running, and reports each overlap with its duration. The
# `overlaps` run is kept as a cross-check over the survivors. L1 answers the convergence half,
# measured from the `db-cut-heal` mark.
#
# ARGUMENTS
#
#   ./scripts/db-partition.sh [hold-seconds] [settle-seconds]
#     hold     how long the leader stays cut off; default 180.
#     settle   how long to watch after the heal; default 120. Give the bounce arm 300+ -- the
#              measurement there is the post-heal convergence, and E3 has seen tails of minutes.
#   BOUNCE=1 ./scripts/db-partition.sh 240 300
#              restart a non-isolated peer at the midpoint of the hold. BOUNCE=<pod> names one;
#              BOUNCE=1 picks any live churnsim pod that is not the leader.
#   DRY_RUN=1 ./scripts/db-partition.sh    resolve the leader, the pg pod and the cut, print them,
#                                          and stop before arming. Captures nothing, cuts nothing.
#   OUT=<dir> ./scripts/db-partition.sh    where the run goes; default runs/db-partition-<stamp>.
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"
source scripts/backend.sh
require_safetylab

HOLD="${1:-180}"
SETTLE="${2:-120}"
POLL=5

refuse() { echo "db-partition: REFUSED — $*" >&2; exit 2; }

BACKEND=$(sim_backend)
[ "$BACKEND" = "postgres" ] \
    || refuse "this experiment is the PostgreSQL one (found $BACKEND). The RavenDB store partition is E7 — ./scripts/split-brain.sh"

# The leader, and the backend holding its lock. `lock-chaos status` prints pid, client address and
# pod for every backend on the leadership lock; the first line is the granted holder. Resolving
# the victim through the same verb the lock experiments use means "the leader" means one thing.
LOCK=$("$SAFETYLAB" lock-chaos status 2>/dev/null | head -1) \
    || refuse "nothing holds the leadership advisory lock — the cluster has no leader, so there is no leader to cut off"
[ -n "$LOCK" ] && [ "$LOCK" != "none" ] \
    || refuse "nothing holds the leadership advisory lock — the cluster has no leader to cut off"

VICTIM=$(cut -f3 <<<"$LOCK")
VICTIM_PID=$(cut -f1 <<<"$LOCK")
VICTIM_ADDR=$(cut -f2 <<<"$LOCK")
[ -n "$VICTIM" ] && [ "$VICTIM" != "unknown-pod" ] \
    || refuse "the lock is held from $VICTIM_ADDR, which does not resolve to a live churnsim pod — a stale registration? Refusing to guess which pod to cut"

PGPOD=$(_pgpod) || refuse "no live PostgreSQL pod"

# Resolved BEFORE the capture starts and before anything is armed, so a bad BOUNCE is a refusal
# rather than a half-run: by the time the midpoint arrives the cut is on and the run is committed.
BOUNCE_POD=""
if [ -n "${BOUNCE:-}" ]; then
    case "$BOUNCE" in
        1|auto|yes)
            BOUNCE_POD=$("$SAFETYLAB" pods --label app=churnsim | grep -v "^${VICTIM}$" | head -1)
            [ -n "$BOUNCE_POD" ] || refuse "no live churnsim pod other than the leader to bounce" ;;
        *)
            BOUNCE_POD="$BOUNCE"
            "$SAFETYLAB" pods --label app=churnsim | grep -qx "$BOUNCE_POD" \
                || refuse "BOUNCE names '$BOUNCE_POD', which is not a live churnsim pod" ;;
    esac

    [ "$BOUNCE_POD" != "$VICTIM" ] \
        || refuse "BOUNCE names the leader ($VICTIM), which is the pod being cut off. Bouncing it would end the partition rather than disturb the roster under it"
fi

if [ -n "${DRY_RUN:-}" ]; then
    echo "== E9 db-partition DRY RUN =="
    echo "  leader   : $VICTIM ($VICTIM_ADDR), holding advisory lock on backend pid $VICTIM_PID"
    echo "  store    : $PGPOD"
    echo "  would cut: $VICTIM  <-/->  $PGPOD   (both directions, FORWARD chain)"
    [ -n "$BOUNCE_POD" ] && echo "  would bounce: $BOUNCE_POD at the midpoint of the hold (graceful delete)"
    echo
    echo "DRY_RUN set — nothing was armed."
    exit 0
fi

NAME="${OUT:-runs/db-partition-$(date -u +%Y%m%dT%H%M%SZ)}"
NAME="${NAME#runs/}"
OUT="runs/$NAME"

if ! "$SAFETYLAB" partition status >/dev/null; then
    refuse "a partition is still armed from an earlier run. Look at what it was measuring, then: just partition-heal"
fi
[ -f runs/.active ] && refuse "a capture is already active ($(cat runs/.active)); stop it first"
[ -d "$OUT" ] && refuse "$OUT already exists; pick another OUT"

echo "== E9 app-to-store partition on $BACKEND =="
echo "hold ${HOLD}s, settle ${SETTLE}s, run directory $OUT"
echo "leader $VICTIM ($VICTIM_ADDR) holds the lock on backend pid $VICTIM_PID; store is $PGPOD"
echo

echo "-- settling --"
settled=$("$SAFETYLAB" settle) || refuse "the cluster did not settle (exit $?); cutting off an unsettled cluster is not this experiment"
echo "settled after ${settled}s"
echo

# A pod replaced during the hold comes back on a new IP, outside every rule, and has escaped the
# cut for the rest of the run (docs/harness-traps.md). Counted before and after rather than assumed.
restarts() { $K get pod "$VICTIM" -o jsonpath='{.status.containerStatuses[0].restartCount}' 2>/dev/null; }
RESTARTS_BEFORE=$(restarts)

./scripts/monitor.sh start "$NAME" || exit 2
mkdir -p "$OUT"
{
    echo "backend=$BACKEND"
    echo "hold_s=$HOLD"
    echo "settle_s=$SETTLE"
    echo "leader_pod=$VICTIM"
    echo "leader_addr=$VICTIM_ADDR"
    echo "lock_pid=$VICTIM_PID"
    echo "pg_pod=$PGPOD"
    echo "restarts_before=$RESTARTS_BEFORE"
    echo "bounce_pod=${BOUNCE_POD:-none}"
} > "$OUT/plan.txt"

TSV="$OUT/timeline.tsv"
printf 'elapsed_s\tphase\tlock_pid\tlock_pod\tleader_pod\tvictim_hb_age_s\tplaced\n' > "$TSV"

# The victim's heartbeat AGE, computed entirely inside the database: now() and health_check are
# both the database's clock, so nothing here compares two clocks (docs/harness-traps.md). A rising
# age is the cut working; it is also exactly what K2 reads out of the capture afterwards.
row() {
    local phase="$1"
    local elapsed="$2"
    local lock pid pod leader age placed
    lock=$("$SAFETYLAB" lock-chaos status 2>/dev/null | head -1)
    if [ -z "$lock" ] || [ "$lock" = "none" ]; then
        pid="none"; pod="-"
    else
        pid=$(cut -f1 <<<"$lock"); pod=$(cut -f3 <<<"$lock")
    fi
    leader=$(_psql "select coalesce(n.description, 'none')
                      from wolverine.wolverine_node_assignments a
                      left join wolverine.wolverine_nodes n on n.id = a.node_id
                     where a.id like 'wolverine://leader%';" | head -1)
    age=$(_psql "select coalesce(max(extract(epoch from (now() - health_check))::int)::text, '-')
                   from wolverine.wolverine_nodes where description = '$VICTIM';" | tr -d '[:space:]')
    placed=$(db_placed)
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$elapsed" "$phase" "${pid:-?}" "${pod:-?}" "${leader:-none}" "${age:--}" "${placed:-?}" >> "$TSV"
    echo "  t+${elapsed}s $phase  lock=${pid:-?}@${pod:-?}  leader=${leader:-none}  victim_hb=${age:--}s  placed=${placed:-?}"
}

HEALED=""
heal() {
    # Runs on every exit path. Healing an intact node is a success, so this is safe to call twice.
    if [ -z "$HEALED" ]; then
        HEALED=1
        echo
        echo "== healing the cut =="
        "$SAFETYLAB" partition heal
        ./scripts/monitor.sh mark db-cut-heal >/dev/null 2>&1 || true
    fi
}
# EXIT heals on the normal path. INT and TERM heal, STOP THE CAPTURE, and exit.
#
# Two separate traps because a handler RETURNS to where it was interrupted: `trap heal EXIT INT
# TERM` alone means a Ctrl-C (or a `timeout`) lifts the cut and then carries on running the
# experiment against an intact cluster, which measures the wrong thing and says nothing about it.
# Found the hard way on db-partition.sh, 2026-09-19.
#
# And the capture has to be stopped on that path too. Exiting straight out would leave runs/.active
# set with the detached follower processes still running -- which is the leaked-follower trap that
# appended one run's pods into the next one's directory for twenty minutes. The evidence captured
# so far is kept; only the collectors are shut down.
abort() {
    heal
    ./scripts/monitor.sh stop >/dev/null 2>&1 || true
    exit "$1"
}
trap heal EXIT
trap 'abort 130' INT
trap 'abort 143' TERM

./scripts/monitor.sh mark settled >/dev/null
row before 0

echo
echo "== cutting $VICTIM off from $PGPOD =="
"$SAFETYLAB" partition arm --isolate "$VICTIM" --from "$PGPOD" | tee "$OUT/partition.txt" || {
    echo "db-partition: the cut did not arm; nothing was measured" >&2
    exit 2
}
./scripts/monitor.sh mark db-cut-start >/dev/null
T_START=$(date -u +%Y-%m-%dT%H:%M:%SZ)
echo "db_cut_start=$T_START" >> "$OUT/plan.txt"

echo
echo "== holding for ${HOLD}s =="
START=$(date +%s)
BOUNCE_AT=$(( HOLD / 2 ))
BOUNCED=""
while :; do
    elapsed=$(( $(date +%s) - START ))
    [ "$elapsed" -ge "$HOLD" ] && break

    # Restart a peer WHILE nothing can redistribute. Graceful (the default delete, 30s grace):
    # the pod deletes its own node row on shutdown and the ON DELETE CASCADE takes its assignment
    # rows with it, so its agents end up owned by nobody and running nowhere. A SIGKILL would
    # leave the row behind and produce the assigned-but-not-running wedge instead -- a different
    # fingerprint, and not the one this arm is for.
    if [ -n "$BOUNCE_POD" ] && [ -z "$BOUNCED" ] && [ "$elapsed" -ge "$BOUNCE_AT" ]; then
        BOUNCED=1
        echo
        echo "== bouncing peer $BOUNCE_POD at t+${elapsed}s (the leader cannot redistribute) =="
        $K delete pod "$BOUNCE_POD" --wait=true 2>&1 | sed 's/^/  /'
        ./scripts/monitor.sh mark peer-bounced >/dev/null
        echo "peer_bounced_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$OUT/plan.txt"

        # The replacement must actually arrive, or this arm measured a 2-pod cluster rather than
        # a roster that moved. It becomes Ready without reaching the database -- readiness here is
        # the container running -- so it should turn up even while the leader is cut off.
        if wait_ready app=churnsim 3 180 >/dev/null 2>&1; then
            echo "  replacement pod is Ready"
        else
            echo "  WARNING: no replacement pod became Ready within 180s; this arm is now measuring" >&2
            echo "           a smaller cluster, not a roster that moved under the leader" >&2
        fi
        echo
    fi

    row cut "$elapsed"
    sleep "$POLL"
done

heal
T_HEAL=$(date -u +%Y-%m-%dT%H:%M:%SZ)
echo "db_cut_heal=$T_HEAL" >> "$OUT/plan.txt"

echo
echo "== watching the heal for ${SETTLE}s =="
HEAL_AT=$(date +%s)
while :; do
    elapsed=$(( $(date +%s) - START ))
    [ $(( $(date +%s) - HEAL_AT )) -ge "$SETTLE" ] && break
    row healed "$elapsed"
    sleep "$POLL"
done

echo
echo "== evidence =="
./scripts/capture-logs.sh "$OUT/post"

"$SAFETYLAB" overlaps "$OUT/post" > "$OUT/overlaps.txt" 2>&1
OVERLAPS_RC=$?
echo "overlaps: exit $OVERLAPS_RC (0 never, 1 overlap found, 2 could not analyse) -> $OUT/overlaps.txt"

RESTARTS_AFTER=$(restarts)
echo "restarts_after=$RESTARTS_AFTER" >> "$OUT/plan.txt"

./scripts/monitor.sh stop
echo
./scripts/monitor.sh check "$NAME" | tee "$OUT/report.txt"
CHECK_RC=${PIPESTATUS[0]}

echo
echo "======== RESULT ========"
echo "  run                : $OUT"
echo "  leader cut off     : $VICTIM ($VICTIM_ADDR), lock on backend pid $VICTIM_PID"
echo "  window             : $T_START -> $T_HEAL (${HOLD}s)"
case "$OVERLAPS_RC" in
    0) echo "  duplicate agents   : none in the post-cut logs" ;;
    1) echo "  duplicate agents   : FOUND — see $OUT/overlaps.txt" ;;
    *) echo "  duplicate agents   : COULD NOT ANALYSE (exit $OVERLAPS_RC) — see $OUT/overlaps.txt" ;;
esac
echo "  victim restarts    : ${RESTARTS_BEFORE:-?} -> ${RESTARTS_AFTER:-?}"
if [ -n "$BOUNCE_POD" ]; then
    echo "  peer bounced       : $BOUNCE_POD at t+${BOUNCE_AT}s of the hold"
    # The drop is the whole point of the arm: agents owned by nobody, with no actor able to place
    # them. Read straight out of the timeline rather than recomputed, so the printed number and the
    # filed one are the same number.
    echo "  placed, min->last  : $(awk -F"\t" 'NR>1{print $7}' "$TSV" | sort -n | head -1) -> $(awk -F"\t" 'END{print $7}' "$TSV")"
fi
if [ -n "$RESTARTS_AFTER" ] && [ -n "$RESTARTS_BEFORE" ] && [ "$RESTARTS_AFTER" -gt "$RESTARTS_BEFORE" ]; then
    echo
    echo "  *** SUSPECT RUN ***  the victim's container restarted during the run. A replacement pod"
    echo "  comes back on a NEW IP, outside every firewall rule, so it escaped the cut for the rest"
    echo "  of the hold. Read the timeline before trusting the window above."
fi
echo "  checkers           : exit $CHECK_RC — $OUT/report.txt"
echo "  timeline           : $TSV"
echo
echo "  Read K2 first: it says whether the cut actually took (a node stopped heartbeating). Then"
echo "  S11 — was the lock held throughout by that silent node, which is the stall this experiment"
echo "  is for — and L1 for whether the cluster ever came back."
if [ -n "$BOUNCE_POD" ]; then
    echo
    echo "  On this arm the measurement is AFTER the heal: L1 for whether it converged and how"
    echo "  long it took (measured from the db-cut-heal mark), and S5 for duplicates WITH their"
    echo "  durations. S5, not overlaps.txt: the bounced pod is gone, so post/ has no log for it,"
    echo "  while the capture's harvested agent events still carry everything it did."
fi
