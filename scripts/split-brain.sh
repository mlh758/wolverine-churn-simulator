#!/usr/bin/env bash
# E7 — split brain on a replicated RavenDB store: isolate the leader's store member (and the
# leader with it) from the other two, hold the cut past the compare-exchange lock's expiry, heal,
# and check what leader election and agent assignment did on each side.
#
# DEPENDS ON  `just deploy-ravendb-cluster` (k8s/ravendb-cluster.yaml + churnsim-ravendb-cluster.yaml
#             + the per-member monitor), $SAFETYLAB, minikube ssh with iptables-nft on the node.
# REQUIRES    a settled cluster, no partition already armed, no capture already active, every
#             member seeing the same Raft leader. Each is a refusal, not a warning.
# PRODUCES    runs/<name>/ with the monitor's per-member history, every pod's log, phase marks,
#             timeline.tsv (one row per 10s poll, one column group per member), post/ (raw pod logs
#             after the heal), overlaps.txt, conflicts.tsv, report.txt. MUTATES the cluster: it
#             writes DROP rules into the node's FORWARD chain, and removes them from a trap.
#
# WHY THE LEADER'S SIDE IS THE MINORITY. The question is what a leader does when its store still
# accepts its writes but can no longer commit to Raft. Its compare-exchange renewals fail, its
# HasLeadershipLock() keeps saying yes off a local field, and its plain-session assignment writes
# succeed on its own member. Meanwhile the majority can take the lock over once it lapses. Putting
# the leader on the majority side would measure a follower losing its store, which is a different
# and less interesting experiment. If the current leader sits on ravendb-0 — the monitor's primary
# view, which must stay on the majority side so the checker-facing fields read the majority — it
# is handed over gracefully (an ordinary pod delete) until it does not.
#
# WHY THE CHURNSIM POD IS CUT TOO. Belt and braces: RAVENDB_PIN_NODE already stops the client
# learning the other members, but a partition that only cut store-to-store links would leave the
# question "did the client reroute?" open, and P1 cannot answer it. Cutting the pod from the
# majority members closes it.
#
# TWO SHAPES OF CUT, chosen with CUT=zone (default) or CUT=store:
#
#   zone    the leader's store member AND the leader's pod are isolated together from the other
#           four pods. Models a zone-level partition: an application node with its nearest store
#           member on one side. No client can route around it. Requires RAVENDB_PIN_NODE=true on
#           the pods, so that "the leader's member" is a fact rather than the default client's
#           choice; the pin decides which pod is stranded, not whether one is.
#   store   ONLY store members are cut from each other; every pod can still reach every member.
#           Models a store-tier partition, and asks whether the default RavenDB client routes
#           around it. Requires RAVENDB_PIN_NODE=false, and isolates the member the default client
#           actually prefers (`safetylab raven-cluster preferred`: the first node of the database
#           group's topology, which every un-pinned client talks to) — isolating any other member
#           would cut one nobody uses and measure nothing.
#
# Both verify the pin from the pods' own CONFIG lines before doing anything, because a store cut
# against pinned pods or a zone cut against un-pinned pods is a run filed under the wrong arm.
#
# ARGUMENTS
#
#   ./scripts/split-brain.sh [hold-seconds] [settle-seconds]
#     hold     how long the cut stands; default 420, past the 300s lock expiry so the majority
#              gets to take the lock over while the old leader still believes. Under 300 is the
#              other arm of the experiment and worth running too.
#     settle   how long to watch after the heal; default 300.
#   CUT=zone|store   see above; default zone.
#   OUT=<dir>  where the run goes; default runs/split-brain-<utc stamp>. Must be under runs/.
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"
source scripts/backend.sh
require_safetylab

HOLD="${1:-420}"
SETTLE="${2:-300}"
CUT="${CUT:-zone}"
case "$CUT" in zone|store) ;; *) echo "split-brain: CUT must be zone or store, not '$CUT'" >&2; exit 2 ;; esac
NAME="${OUT:-runs/split-brain-$(date -u +%Y%m%dT%H%M%SZ)}"
NAME="${NAME#runs/}"
OUT="runs/$NAME"
POLL=10

member_url() { echo "http://ravendb-$1.ravendb.default.svc.cluster.local:8080"; }

refuse() { echo "split-brain: REFUSED — $*" >&2; exit 2; }
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# ------------------------------------------------------------------ preconditions

BACKEND=$(sim_backend)
TOPOLOGY=$(sim_topology)
[ "$BACKEND" = "ravendb" ] && [ "$TOPOLOGY" = "cluster" ] \
    || refuse "this experiment needs the replicated RavenDB arm (found $BACKEND/$TOPOLOGY). just deploy-ravendb-cluster"

if ! "$SAFETYLAB" partition status >/dev/null; then
    refuse "a partition is still armed from an earlier run. Look at what it was measuring, then: just partition-heal"
fi

[ -f runs/.active ] && refuse "a capture is already active ($(cat runs/.active)); stop it first"
[ -d "$OUT" ] && refuse "$OUT already exists; pick another OUT"

echo "== E7 split brain on $BACKEND/$TOPOLOGY, $CUT cut =="
echo "hold ${HOLD}s, settle ${SETTLE}s, run directory $OUT"
echo

# The pin has to match the cut, and it is read off the pods' own startup output, not the manifest.
echo "-- pods --"
case "$CUT" in
    zone)  "$SAFETYLAB" verify-config --label app=churnsim --expect RAVENDB_PIN_NODE=true \
               || refuse "a zone cut needs RAVENDB_PIN_NODE=true on every pod (the leader's member must be a fact)" ;;
    store) "$SAFETYLAB" verify-config --label app=churnsim --expect RAVENDB_PIN_NODE=false \
               || refuse "a store cut needs RAVENDB_PIN_NODE=false on every pod (the question is what the default client does)" ;;
esac
echo

echo "-- cluster --"
_raven raven-cluster status || refuse "the RavenDB cluster is not healthy — every member must see a leader and the database must be served by three members before a partition means anything"
echo

echo "-- settling --"
settled=$("$SAFETYLAB" settle) || refuse "the cluster did not settle (exit $?); a partition of an unsettled cluster is not this experiment"
echo "settled after ${settled}s"

# ------------------------------------------------------------------ choose the sides

# The leader's pod index is the minority side. ravendb-0 is the monitor's primary view and must
# stay on the majority, so a leader on churnsim-0 is handed over (gracefully — this is set-up,
# not the nemesis) until it is elsewhere.
leader_index() {
    local row pod
    row=$(_raven query leader --url "$(member_url 0)" 2>/dev/null) || return 1
    [ "$row" = "none" ] && return 1
    pod=$(cut -f1 <<<"$row")
    case "$pod" in
        churnsim-[0-9]) echo "${pod#churnsim-}" ;;
        *) return 1 ;;
    esac
}

LEADER_POD=""
if [ "$CUT" = "zone" ]; then
    attempts=0
    while :; do
        K_IDX=$(leader_index) || refuse "could not resolve the leader to a churnsim-N pod"
        [ "$K_IDX" != "0" ] && break

        attempts=$((attempts + 1))
        [ "$attempts" -le 4 ] || refuse "the leader stayed on churnsim-0 through 4 graceful handovers; ravendb-0 must stay on the majority side"
        echo "leader is churnsim-0 (the primary view's member); handing over gracefully ($attempts/4)"
        $K delete pod churnsim-0 --wait=true >/dev/null
        wait_ready app=churnsim 3 300 >/dev/null || refuse "churnsim-0 did not come back after the handover"
        "$SAFETYLAB" settle >/dev/null || refuse "the cluster did not re-settle after the handover"
    done
    LEADER_POD="churnsim-$K_IDX"

    MINORITY="ravendb-$K_IDX,churnsim-$K_IDX"
    MAJORITY=""
    for i in 0 1 2; do
        [ "$i" = "$K_IDX" ] && continue
        MAJORITY="${MAJORITY:+$MAJORITY,}ravendb-$i,churnsim-$i"
    done
else
    # The member every un-pinned client is on. Pods are not cut at all.
    preferred=$(_raven raven-cluster preferred --url "$(member_url 0)") || refuse "could not read the preferred member"
    case "$preferred" in
        ravendb-[0-9]) K_IDX="${preferred#ravendb-}" ;;
        *) refuse "unexpected preferred member '$preferred'" ;;
    esac
    if [ "$K_IDX" = "0" ]; then
        echo "WARNING: the preferred member is ravendb-0, the monitor's primary view; the checker-facing fields read the MINORITY for this run" >&2
    fi
    row0=$(_raven query leader --url "$(member_url 0)" 2>/dev/null); LEADER_POD=$(cut -f1 <<<"$row0")

    MINORITY="ravendb-$K_IDX"
    MAJORITY=""
    for i in 0 1 2; do
        [ "$i" = "$K_IDX" ] && continue
        MAJORITY="${MAJORITY:+$MAJORITY,}ravendb-$i"
    done
fi

echo
echo "cut:       $CUT"
echo "leader:    ${LEADER_POD:-unknown}"
[ "$CUT" = "store" ] && echo "preferred: ravendb-$K_IDX  (the member every un-pinned client is on)"
echo "minority:  $MINORITY"
echo "majority:  $MAJORITY"
echo

# ------------------------------------------------------------------ capture

./scripts/monitor.sh start "$NAME" || exit 2
mkdir -p "$OUT"
{
    echo "cut=$CUT"
    echo "hold_s=$HOLD"
    echo "settle_s=$SETTLE"
    echo "leader_pod=${LEADER_POD:-unknown}"
    echo "minority=$MINORITY"
    echo "majority=$MAJORITY"
    echo "primary_view=ravendb-0"
} > "$OUT/plan.txt"

TSV="$OUT/timeline.tsv"
{
    printf 'elapsed_s\tphase'
    for i in 0 1 2; do printf '\tlock_%s\tttl_%s\tleader_%s\tplaced_%s' "$i" "$i" "$i" "$i"; done
    printf '\n'
} > "$TSV"

# One row per poll: per member, who it says holds the lock (node id prefix, or none/expired),
# seconds to expiry, who owns the leader row, and how many agents are placed. The monitor's
# history has all of this per second; this is the glance-at-it view and the thing to read while
# the run is still going.
row() {
    # Split, not one `local` line: bash expands every word before assigning any (harness-traps.md).
    local phase="$1"
    local elapsed="$2"
    local line="$elapsed	$phase"
    for i in 0 1 2; do
        local url lock holder ttl leader pod placed
        url=$(member_url "$i")
        lock=$(_raven query lock --url "$url" 2>/dev/null | head -1)
        if [ -z "$lock" ] || [ "$lock" = "none" ]; then
            holder="none"; ttl="-"
        else
            holder=$(cut -f2 <<<"$lock" | cut -c1-8)
            ttl=$(cut -f4 <<<"$lock")
        fi
        leader=$(_raven query leader --url "$url" 2>/dev/null)
        if [ -z "$leader" ] || [ "$leader" = "none" ]; then pod="none"; else pod=$(cut -f1 <<<"$leader"); fi
        placed=$(_raven query placed --url "$url" 2>/dev/null | tr -d '[:space:]')
        line="$line	${holder:-?}	${ttl:-?}	${pod:-?}	${placed:-?}"
    done
    printf '%s\n' "$line" >> "$TSV"
    echo "  t+${elapsed}s $phase  $(cut -f3- <<<"$line" | tr '\t' ' ')"
}

# ------------------------------------------------------------------ the nemesis

HEALED=""
heal() {
    # Runs on every exit path. Healing an intact node is a success, so this is safe to call twice.
    if [ -z "$HEALED" ]; then
        HEALED=1
        echo
        echo "== healing the partition =="
        "$SAFETYLAB" partition heal
        ./scripts/monitor.sh mark partition-heal >/dev/null 2>&1 || true
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
echo "== arming the partition =="
"$SAFETYLAB" partition arm --isolate "$MINORITY" --from "$MAJORITY" | tee "$OUT/partition.txt" || {
    echo "split-brain: the partition did not arm; nothing was measured" >&2
    exit 2
}
./scripts/monitor.sh mark partition-start >/dev/null
T_START=$(now)
echo "partition_start=$T_START" >> "$OUT/plan.txt"

echo
echo "== holding for ${HOLD}s =="
START=$(date +%s)
while :; do
    elapsed=$(( $(date +%s) - START ))
    [ "$elapsed" -ge "$HOLD" ] && break
    row cut "$elapsed"
    sleep "$POLL"
done

heal
T_HEAL=$(now)
echo "partition_heal=$T_HEAL" >> "$OUT/plan.txt"

echo
echo "== watching the heal for ${SETTLE}s =="
HEAL_AT=$(date +%s)
while :; do
    elapsed=$(( $(date +%s) - START ))
    [ $(( $(date +%s) - HEAL_AT )) -ge "$SETTLE" ] && break
    row healed "$elapsed"
    sleep "$POLL"
done

# ------------------------------------------------------------------ evidence

echo
echo "== post-heal evidence =="
./scripts/capture-logs.sh "$OUT/post"

"$SAFETYLAB" overlaps "$OUT/post" > "$OUT/overlaps.txt" 2>&1
OVERLAPS_RC=$?
echo "overlaps: exit $OVERLAPS_RC (0 never, 1 overlap found, 2 could not analyse) -> $OUT/overlaps.txt"

_raven query conflicts --url "$(member_url 0)" --since "$T_START" > "$OUT/conflicts.tsv" 2> "$OUT/conflicts.err"
CONFLICTS_RC=$?
echo "conflicts: exit $CONFLICTS_RC (0 none, 1 found, 2 could not ask) -> $OUT/conflicts.tsv"

./scripts/monitor.sh stop
echo
./scripts/monitor.sh check "$NAME" | tee "$OUT/report.txt"
CHECK_RC=${PIPESTATUS[0]}

# ------------------------------------------------------------------ summary

echo
echo "======== RESULT ========"
echo "  run                : $OUT"
echo "  cut                : $CUT"
echo "  leader             : ${LEADER_POD:-unknown}"
echo "  minority           : $MINORITY"
echo "  window             : $T_START -> $T_HEAL (${HOLD}s)"
case "$OVERLAPS_RC" in
    0) echo "  duplicate agents   : none in the post-heal logs" ;;
    1) echo "  duplicate agents   : FOUND — see $OUT/overlaps.txt" ;;
    *) echo "  duplicate agents   : COULD NOT ANALYSE (exit $OVERLAPS_RC) — see $OUT/overlaps.txt" ;;
esac
case "$CONFLICTS_RC" in
    0) echo "  document conflicts : none reported by ravendb-0" ;;
    1) echo "  document conflicts : $(grep -c . "$OUT/conflicts.tsv") row(s) — see $OUT/conflicts.tsv" ;;
    *) echo "  document conflicts : COULD NOT ASK (exit $CONFLICTS_RC) — $(head -1 "$OUT/conflicts.err")" ;;
esac
echo "  checkers           : exit $CHECK_RC — $OUT/report.txt (read P1 first: it says whether the cut took)"
echo "  timeline           : $TSV"
