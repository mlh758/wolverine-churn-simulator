#!/usr/bin/env bash
# Does the cluster HEAL a duplicated agent, or stay wrong?
#
# DEPENDS ON  a deployed churnsim cluster (any arm), $SAFETYLAB, scripts/rollout.sh.
# REQUIRES    the cluster settled.
# PRODUCES    a row per iteration appended to runs/heal-test/results.tsv:
#               iteration outcome healed persisted longest_heal_s
#             outcome is never | HEALED | PERSISTED | SKIP-no-settle | SKIP-unanalysable.
#             Raw pod logs and overlaps.txt are kept for every iteration that overlapped.
#
# A single look at the end state cannot answer this: a reconcile sweep that clears a duplicate in
# ~6s (3 health-check ticks at ChurnSim's 2s cadence) makes the end state read clean, and clean is
# indistinguishable from "no duplicate ever happened". Polling every 15s is no better -- a 6s
# window is missed most of the time. So the cluster is not sampled at all: every AGENT-START and
# AGENT-STOP is already in the pod logs with a timestamp, and one capture at the end recovers the
# whole timeline at full resolution.
#
# ARGUMENTS
#
#   ./scripts/heal-test.sh [iterations] [watch-seconds]   defaults 8 and 300; watch-seconds is how
#                                                         long after the rollout to let the cluster
#                                                         act before capturing
#   OUT=<dir>                                             results directory; default runs/heal-test
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

ITERS="${1:-8}"
WATCH="${2:-300}"     # how long after the rollout to let the cluster act before capturing

# One results directory per arm: this TSV carries no backend column, so two arms in one file pool.
OUT="${OUT:-runs/heal-test}"
TSV="$OUT/results.tsv"
mkdir -p "$OUT"
[ -f "$TSV" ] || printf 'iteration\toutcome\thealed\tpersisted\tlongest_heal_s\n' > "$TSV"

# The only question this script asks the store is "how many sim agents are placed"; everything
# else it needs is already in the pod logs.
source scripts/backend.sh
require_safetylab

live_pods() { "$SAFETYLAB" pods --label app=churnsim; }

capture() {
    local dir="$1"
    mkdir -p "$dir"
    for p in $(live_pods); do
        $K logs "$p" > "$dir/raw.$p.jsonl" 2>/dev/null
    done
}

# `safetylab settle`: exit 0 settled (elapsed seconds on stdout), 1 never settled (-1), 2 could
# not measure. The target is the deployment's SIM_AGENT_COUNT, not a literal written into the loop.
wait_settled() { "$SAFETYLAB" settle >/dev/null; }

echo "heal-test: $ITERS rollouts, capturing ${WATCH}s after each"
echo "image: $($K get deployment churnsim -o jsonpath='{.spec.template.spec.containers[0].image}')"

for i in $(seq 1 "$ITERS"); do
    echo "== iteration $i =="
    rm -rf "$OUT/iter$i"; mkdir -p "$OUT/iter$i"

    $K rollout restart deployment/churnsim >/dev/null 2>&1
    $K rollout status deployment/churnsim --timeout=900s >/dev/null 2>&1
    if ! wait_settled; then
        printf '%s\tSKIP-no-settle\t-\t-\t-\n' "$i" >> "$TSV"; tail -1 "$TSV"; continue
    fi
    sleep 30

    # The measured event, then let the cluster do whatever it is going to do.
    ./scripts/rollout.sh "$(date +%s)" >/dev/null 2>&1
    sleep "$WATCH"

    capture "$OUT/iter$i"
    # `safetylab overlaps` writes overlaps.txt and prints this file's columns 2-5:
    #
    #     outcome <TAB> healed <TAB> persisted <TAB> longest_heal_s
    #
    # exit 0 no overlap / 1 overlap found / 2 COULD NOT ANALYSE. The last matters because the
    # branch below deletes the raw logs on "never" -- an analyser that did not answer is not
    # evidence of a healthy cluster.
    row=$("$SAFETYLAB" overlaps "$OUT/iter$i" --tsv); rc=$?

    if [ "$rc" -ge 2 ]; then
        printf '%s\tSKIP-unanalysable\t-\t-\t-\n' "$i" >> "$TSV"
        echo "  the overlap analysis was refused -- evidence kept in $OUT/iter$i/"
        tail -1 "$TSV"; continue
    fi

    printf '%s\t%s\n' "$i" "$row" >> "$TSV"
    tail -1 "$TSV"

    # Nothing overlapped: reclaim the logs. Anything else keeps them.
    if [ "$rc" -eq 0 ]; then
        rm -f "$OUT/iter$i"/raw.*.jsonl
    else
        sed -n '2,6p' "$OUT/iter$i/overlaps.txt"
    fi
done

echo
echo "======== SUMMARY ========"
python3 - "$TSV" <<'PY'
import sys, collections
rows = [l.rstrip("\n").split("\t") for l in open(sys.argv[1])][1:]
c = collections.Counter(r[1] for r in rows if len(r) > 1)
watched = c.get("never", 0) + c.get("HEALED", 0) + c.get("PERSISTED", 0)
print(f"  rollouts watched      : {watched}")
print(f"  no duplicate at all   : {c.get('never', 0)}")
print(f"  duplicated, HEALED    : {c.get('HEALED', 0)}")
print(f"  duplicated, PERSISTED : {c.get('PERSISTED', 0)}")
for k, v in sorted(c.items()):
    if k.startswith("SKIP"):
        print(f"  {k:<22}: {v}")
dupd = c.get("HEALED", 0) + c.get("PERSISTED", 0)
if dupd:
    print(f"  -> of {dupd} rollouts that duplicated, {c.get('HEALED',0)} converged and "
          f"{c.get('PERSISTED',0)} stayed wrong")
PY
echo "HEAL TEST COMPLETE"
