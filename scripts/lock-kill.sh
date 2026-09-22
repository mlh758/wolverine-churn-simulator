#!/usr/bin/env bash
# E8 — what does a PostgreSQL leader do when the connection holding its advisory lock dies under
# it? Kill the backend holding the lock, watch what the cluster does, and check the capture.
#
# DEPENDS ON  a deployed PostgreSQL churnsim cluster, ./scripts/monitor.sh deploy, $SAFETYLAB.
# REQUIRES    the PostgreSQL arm, a settled cluster, a leader whose advisory lock is demonstrably
#             held from the leader pod's own address, and no capture already active. Every one is
#             a refusal: a window timed from a cluster whose lock and leader row already disagree
#             measures a recovery from an unknown starting point, not this fault.
# PRODUCES    runs/<name>/ with the monitor's history, every pod's log, phase marks, timeline.tsv
#             (one row per 5s poll), post/ (raw pod logs), overlaps.txt and report.txt. MUTATES
#             the cluster: it terminates a PostgreSQL backend.
#
# WHY THIS IS A DIFFERENT EXPERIMENT FROM leader-kill.sh. That one kills the leader's PROCESS: the
# node is gone, and the lock goes with it because there is nobody left to hold it. This one leaves
# the node running, healthy, serving agents, and takes only its lock away. What is under test is
# the gap between server-side truth and the node's belief — on PostgreSQL, lock-loss detection is
# a `select 1` liveness ping that deliberately reports the last known state when its gate is busy,
# so "I am the leader" is a lagging belief BY DESIGN, and there are no fencing tokens on the agent
# commands the believer keeps dispatching (docs/experiments.md, facts 1 and 2). That is the
# GH-2602 shape, and this is the cheapest way to put a real cluster into it on demand.
#
# WHY THE FAULT IS A TERMINATED SESSION AND NOT A RELEASED LOCK. A session-level advisory lock has
# no expiry and no owner column; the death of its session IS its release. An operator cannot
# remove somebody else's: `pg_advisory_unlock(id)` only ever touches the CALLING session's lock
# table, so from psql it releases nothing, returns false, and says so in a WARNING on stderr. An
# injector built that way runs its full duration, injects nothing, and leaves a plausible number
# behind — harness-traps.md rule 4. So a timed-out connection, a bounced pooler, an operator
# killing a session and a network blip all arrive at the server as the same event, and
# pg_terminate_backend is how this rig reproduces it.
#
# ARGUMENTS
#
#   ./scripts/lock-kill.sh [watch-seconds]       how long to watch after the kill; default 180.
#   MODE=cancel ./scripts/lock-kill.sh           THE CONTROL ARM: pg_cancel_backend interrupts the
#                                                statement and leaves the session, so the lock
#                                                does NOT move. "A blip is not a lost lock" is the
#                                                thing a finding from the default arm needs to be
#                                                a finding against.
#   DRY_RUN=1 ./scripts/lock-kill.sh             resolve the leader and the backend holding its
#                                                lock, print them, and stop before signalling.
#                                                Captures nothing and mutates nothing.
#   OUT=<dir> ./scripts/lock-kill.sh             where the run goes; default runs/lock-kill-<stamp>.
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"
source scripts/backend.sh

# $SAFETYLAB owns the target resolution and every refusal in it, so it is not optional.
require_safetylab

WATCH="${1:-180}"
MODE="${MODE:-terminate}"
case "$MODE" in terminate|cancel) ;; *) echo "lock-kill: MODE must be terminate or cancel, not '$MODE'" >&2; exit 2 ;; esac
POLL=5

refuse() { echo "lock-kill: REFUSED — $*" >&2; exit 2; }

BACKEND=$(sim_backend)
[ "$BACKEND" = "postgres" ] \
    || refuse "this experiment is PostgreSQL only (found $BACKEND). RavenDB's leadership lock is a compare-exchange document with an expiry and no session to terminate — see S8 and E7. MySQL's named lock IS session-scoped and KILL <connection> is the same fault, but nothing here injects it yet"

# ------------------------------------------------------------------ dry run

# Resolves the leader, the backend holding its lock, and every refusal, without capturing or
# signalling anything. A fault injector ought to be able to show what it would destroy.
if [ -n "${DRY_RUN:-}" ]; then
    echo "== lock-kill DRY RUN =="
    "$SAFETYLAB" lock-chaos status || true
    echo
    exec "$SAFETYLAB" lock-chaos kill --mode "$MODE" --dry-run
fi

NAME="${OUT:-runs/lock-kill-$(date -u +%Y%m%dT%H%M%SZ)}"
NAME="${NAME#runs/}"
OUT="runs/$NAME"

[ -f runs/.active ] && refuse "a capture is already active ($(cat runs/.active)); stop it first"
[ -d "$OUT" ] && refuse "$OUT already exists; pick another OUT"

echo "== E8 lock-session kill on $BACKEND, mode $MODE =="
echo "watch ${WATCH}s, run directory $OUT"
echo

# ------------------------------------------------------------------ preconditions

echo "-- settling --"
settled=$("$SAFETYLAB" settle) || refuse "the cluster did not settle (exit $?); a lock kill against an unsettled cluster is not this experiment"
echo "settled after ${settled}s"
echo

# The dry run IS the precondition check: it refuses when nothing holds the lock, when a waiter is
# the only candidate, when the holder's address is not the leader pod's, and when the leader row
# does not resolve to a live pod. Running it before the capture starts means a refusal leaves no
# half-marked run directory behind.
echo "-- target --"
TARGET=$("$SAFETYLAB" lock-chaos kill --mode "$MODE" --dry-run) \
    || refuse "no usable target (see above). The lock and the leader row must agree before this fault means anything"
VICTIM_PID=$(cut -f1 <<<"$TARGET")
VICTIM_ADDR=$(cut -f2 <<<"$TARGET")
VICTIM_POD=$(cut -f3 <<<"$TARGET")
echo

# ------------------------------------------------------------------ capture

./scripts/monitor.sh start "$NAME" || exit 2
mkdir -p "$OUT"
{
    echo "backend=$BACKEND"
    echo "mode=$MODE"
    echo "watch_s=$WATCH"
    echo "leader_pod=$VICTIM_POD"
    echo "lock_pid=$VICTIM_PID"
    echo "lock_addr=$VICTIM_ADDR"
} > "$OUT/plan.txt"

TSV="$OUT/timeline.tsv"
printf 'elapsed_s\tphase\tlock_pid\tlock_pod\tleader_pod\tplaced\n' > "$TSV"

# One row per poll: who holds the advisory lock right now, who the assignment table says leads,
# and how many agents are placed. The monitor's history has all of this per second; this is the
# glance-at-it view while the run is still going.
row() {
    # Split, not one `local` line: bash expands every word before assigning any (harness-traps.md).
    local phase="$1"
    local elapsed="$2"
    local lock pid pod leader placed
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
    placed=$(db_placed)
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$elapsed" "$phase" "${pid:-?}" "${pod:-?}" "${leader:-none}" "${placed:-?}" >> "$TSV"
    echo "  t+${elapsed}s $phase  lock=${pid:-?}@${pod:-?}  leader=${leader:-none}  placed=${placed:-?}"
}

./scripts/monitor.sh mark settled >/dev/null
row before 0

# ------------------------------------------------------------------ the nemesis

echo
echo "== $MODE on pid $VICTIM_PID ($VICTIM_POD, the leader) =="

# The mark goes in BEFORE the signal, and the order is load-bearing. K1 takes the pid holding the
# lock at the last sample AT OR BEFORE the mark and asserts what the ARM claims about it.
# Marking after the kill would let a sample showing the leader's NEW connection be the "before"
# one, and the check would then report a fault that fired as one that did not.
#
# The mark NAMES the arm, because the two arms assert opposite things and one label cannot carry
# both: a `lock-kill` mark on a cancel run made K1 fail every correct control arm and explain it
# as "the session was never terminated" — about a run that never tried to terminate one.
case "$MODE" in
    cancel) MARK=lock-cancel ;;
    *)      MARK=lock-kill ;;
esac
./scripts/monitor.sh mark "$MARK" >/dev/null
T_KILL=$(date -u +%Y-%m-%dT%H:%M:%SZ)
echo "lock_kill=$T_KILL" >> "$OUT/plan.txt"

"$SAFETYLAB" lock-chaos kill --mode "$MODE" | tee "$OUT/kill.txt"
KILL_RC=${PIPESTATUS[0]}

# Exit 2 here is not "the command failed": it is the injector having read the lock back from the
# server and found it did not move. There is nothing to watch, and the capture is kept as the
# evidence of what the cluster was doing instead.
if [ "$KILL_RC" -ne 0 ]; then
    echo
    echo "*** INVALID RUN *** the fault did not fire (exit $KILL_RC, see above). Not watching a" >&2
    echo "cluster that never lost its lock; the capture is kept in $OUT." >&2
    ./scripts/monitor.sh stop
    ./scripts/monitor.sh check "$NAME" | tee "$OUT/report.txt"
    exit 2
fi

echo
echo "== watching for ${WATCH}s =="
START=$(date +%s)
while :; do
    elapsed=$(( $(date +%s) - START ))
    [ "$elapsed" -ge "$WATCH" ] && break
    row after "$elapsed"
    sleep "$POLL"
done

# ------------------------------------------------------------------ evidence

echo
echo "== evidence =="
./scripts/capture-logs.sh "$OUT/post"

# A leader that keeps dispatching agent commands after losing its lock is the fencing question
# (docs/experiments.md fact 1), and duplicates are what it looks like from the pods.
"$SAFETYLAB" overlaps "$OUT/post" > "$OUT/overlaps.txt" 2>&1
OVERLAPS_RC=$?
echo "overlaps: exit $OVERLAPS_RC (0 never, 1 overlap found, 2 could not analyse) -> $OUT/overlaps.txt"

./scripts/monitor.sh stop
echo
./scripts/monitor.sh check "$NAME" | tee "$OUT/report.txt"
CHECK_RC=${PIPESTATUS[0]}

# ------------------------------------------------------------------ summary

echo
echo "======== RESULT ========"
echo "  run                : $OUT"
echo "  mode               : $MODE"
echo "  victim             : pid $VICTIM_PID on $VICTIM_POD ($VICTIM_ADDR), the leader"
echo "  kill at            : $T_KILL"
case "$OVERLAPS_RC" in
    0) echo "  duplicate agents   : none in the post-kill logs" ;;
    1) echo "  duplicate agents   : FOUND — see $OUT/overlaps.txt" ;;
    *) echo "  duplicate agents   : COULD NOT ANALYSE (exit $OVERLAPS_RC) — see $OUT/overlaps.txt" ;;
esac
echo "  checkers           : exit $CHECK_RC — $OUT/report.txt"
echo "  timeline           : $TSV"
echo
echo "  Read K1 first: on a terminate it says whether the lock actually left the backend holding"
echo "  it, and on a cancel whether the lock correctly stayed put. Then S3 —"
echo "  how long the leader row named a node with no server-side claim to leadership — and L1,"
echo "  for whether the cluster came back at all."
