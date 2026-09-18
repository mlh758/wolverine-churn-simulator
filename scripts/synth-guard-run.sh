#!/usr/bin/env bash
# One arm of the synthetic-self-guard A/B: does the reconcile sweep's guard stop a node from
# stopping its own agents when its snapshot omits itself?
#
# DEPENDS ON  the PostgreSQL arm only (the fault is Postgres row-level security; RavenDB has no
#             counterpart and this refuses to run there), $SAFETYLAB, scripts/rollout.sh, and
#             ./scripts/synth-guard-prep.sh having been run since the last deploy.
# REQUIRES    no chaos already armed on the cluster, and every live pod reporting
#             StaleNodeTimeout=$STALE_TIMEOUT from its own startup output. Both are refusals.
# PRODUCES    runs/synth-guard/<arm>/{during,steady,post}/ with raw pod logs and a
#             running-vs-assigned snapshot each, plus summary.txt with the marker counts.
# GUARANTEES  the fault is disarmed on every exit path, including Ctrl-C and any failure above.
#             A leaked arm leaves one node permanently invisible to the app role, and every later
#             experiment on that cluster then silently measures a crippled node.
#
# Phases: settle -> chaos -> heal -> one rolling deploy -> settle -> tail.
#
# ARGUMENTS
#
#   ./scripts/synth-guard-run.sh <arm-label>   names the output directory, e.g. sweepnoguard
#   STEADY_SECONDS=<n>                         how long the fault stays armed; default 240
#   TAIL_SECONDS=<n>                           settle time after the rollout; default 120
#   STALE_TIMEOUT=<n>                          the StaleNodeTimeout each pod must report; default 4
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

# PostgreSQL only, and not portably so. The fault injected here is row-level security hiding one
# node's row from the app role -- a Postgres feature with no RavenDB counterpart at all. Refuse
# rather than run something that looks like the experiment and is not.
source scripts/backend.sh
if [ "$(sim_backend)" = "ravendb" ]; then
    echo "$(basename "$0"): the synthetic-self-guard runs inject faults with Postgres row-level" >&2
    echo "    security, which RavenDB has no equivalent of. This experiment is PostgreSQL-only." >&2
    exit 2
fi
require_safetylab

ARM="${1:?usage: synth-guard-run.sh <arm-label>}"
STEADY_SECONDS="${STEADY_SECONDS:-240}"
TAIL_SECONDS="${TAIL_SECONDS:-120}"
STALE_TIMEOUT="${STALE_TIMEOUT:-4}"
OUT="runs/synth-guard/$ARM"
mkdir -p "$OUT"

# Every exit path disarms: normal end, `exit 1` anywhere above, Ctrl-C, SIGTERM. Idempotent, so
# the paths where nothing was ever armed cost one no-op psql.
disarm() { "$SAFETYLAB" chaos disarm || echo "  !! COULD NOT DISARM -- do not run another experiment on this cluster" >&2; }
trap disarm EXIT INT TERM

# `safetylab settle`: exit 0 settled (elapsed seconds on stdout), 1 never settled (-1), 2 could
# not measure. The target is the deployment's SIM_AGENT_COUNT, not a literal written into the loop.
wait_settled() { "$SAFETYLAB" settle >/dev/null; }

# `safetylab snapshot` -- 0 clean, 1 diverged, 2 COULD NOT MEASURE. A phase reading
# `duplicated=0 orphaned=0 missing=0` because the store was unreachable is indistinguishable from
# one reading it because the guard worked, and deciding that is this script's whole job.
snapshot() {
    local dir="$1"
    local rc
    "$SAFETYLAB" snapshot "$dir" >/dev/null
    rc=$?
    if [ "$rc" -ge 2 ]; then
        echo "REFUSED -- no measurement was taken for $dir (reason on stderr above)"
        return 2
    fi
    head -1 "$dir/report.txt"
}

# A leaked arm from an earlier run would make this one measure a cluster that already has a node
# hidden -- two victims, and an arm whose numbers mean nothing. Exit 1 from `status` is "armed".
echo "== [$ARM] checking the cluster is not already under a fault =="
if ! "$SAFETYLAB" chaos status >/dev/null; then
    echo "  chaos is already armed on this cluster. A previous run did not disarm." >&2
    echo "  Inspect what it was measuring, then: $SAFETYLAB chaos disarm" >&2
    exit 2
fi
echo "  clean"

# REFUSE, do not warn. `kubectl set env` is silently undone by `kubectl apply`'s three-way merge,
# which has mislabelled an arm here before. Every live pod is checked from its own startup output.
echo "== [$ARM] verifying pod CONFIG =="
"$SAFETYLAB" verify-config --label app=churnsim \
    --expect "StaleNodeTimeout=$STALE_TIMEOUT" || {
    echo "  refusing to measure a cluster that is not configured the way this arm claims." >&2
    echo "  ./scripts/synth-guard-prep.sh sets it." >&2
    exit 2
}

echo "== [$ARM] waiting for initial settle =="
wait_settled || { echo "never settled"; exit 1; }

# Chaos phase: sustained read lag against ONE victim node, via row-level security.
#
# Three deletion-based attempts failed to open the window (runs/synth-guard/*-nochaos, *-slowchaos,
# *-fastdelete): the ~1-2ms gap between a tick's heartbeat upsert and its snapshot read is
# unhittable from outside. RLS reproduces the production condition -- reads lagging writes --
# deterministically. Only the victim's NODE row is hidden, never its assignment rows, so its own
# snapshot omits itself while its agents stay claimed here. Victim selection and its preconditions
# are in `safetylab chaos arm`.
echo "== [$ARM] chaos: RLS read-lag against one non-leader victim, ${STEADY_SECONDS}s =="
VICTIM=$("$SAFETYLAB" chaos arm) || exit 2
echo "  victim node id: $VICTIM"
echo "  armed for ${STEADY_SECONDS}s"
sleep "$STEADY_SECONDS"
snapshot "$OUT/during"

"$SAFETYLAB" chaos disarm || exit 2
echo "  disarmed"
sleep 60   # healing window before judging
snapshot "$OUT/steady"

echo "== [$ARM] rolling deploy =="
./scripts/rollout.sh "$(date +%s)" >/dev/null 2>&1
wait_settled || echo "  WARNING: no settle after rollout"
sleep "$TAIL_SECONDS"
snapshot "$OUT/post"

# `safetylab count` matches the Message FIELD, not any line containing the string, and exits 2 on
# a directory with no logs rather than printing a confident 0.
count_marker() { "$SAFETYLAB" count "$1" --message "$2" 2>/dev/null || echo "?"; }

echo "== [$ARM] summary =="
{
    echo "arm: $ARM"
    echo "captured: $(date -u +%FT%TZ)"
    echo "victim: $VICTIM"
    for phase in during steady post; do
        d="$OUT/$phase"
        [ -d "$d" ] || continue
        inj=$(count_marker "$d" "was missing from its own node snapshot")
        eject=$(count_marker "$d" "re-registered its row after it was deleted")
        stop=$(count_marker "$d" "stopping the local copy")
        start=$(count_marker "$d" "but is not running here; starting it")
        claim=$(count_marker "$d" "restoring this node's claim")
        echo "[$phase] injections=$inj peer_ejections=$eject sweep_stops=$stop sweep_starts=$start sweep_reclaims=$claim"
        echo "[$phase] $(head -1 "$d/report.txt" 2>/dev/null || echo 'no snapshot')"
    done

    # A fault injector must prove it fired. Zero injections means the window never opened, so this
    # arm measured a healthy cluster under a label that says otherwise.
    during_inj=$(count_marker "$OUT/during" "was missing from its own node snapshot")
    if [ "$during_inj" = "0" ]; then
        echo
        echo "*** INVALID ARM ***  the fault was armed but never injected: zero 'missing from its"
        echo "own node snapshot' occurrences during the chaos phase. The guard was not exercised."
        echo "Do not read this arm as evidence that it works."
    elif [ "$during_inj" = "?" ]; then
        echo
        echo "*** INVALID ARM ***  the injection count could not be read at all."
    fi
} | tee "$OUT/summary.txt"
