#!/usr/bin/env bash
# Does the cluster HEAL a duplicated agent, or stay wrong?
#
# The question a convergence fix has to be judged on. `duplicate-rate.sh` looks once after the dust
# settles and cannot answer it: a reconcile sweep that clears a duplicate in ~6s (3 health-check
# ticks at ChurnSim's 2s cadence) makes the end state read clean, and clean is indistinguishable
# from "no duplicate ever happened".
#
# The first version of this script polled the cluster every 15s. That was no better -- a 6s window
# sampled every 15s is missed most of the time, and three "never" results in a row nearly became
# "the fix prevents duplication".
#
# So: do not sample the cluster at all. Every AGENT-START and AGENT-STOP is already in the pod logs
# with a timestamp, so one capture at the end recovers the complete timeline at full resolution.
# scripts/overlaps.py replays it into residency intervals and classifies every overlap:
#
#   HEALED     - both copies ran, then one stopped
#   PERSISTED  - both copies were still running when the log was captured
#
# Stock 6.35.0 PERSISTS: an earlier duplicate was still live 40 minutes on.
#
#   ./scripts/heal-test.sh [iterations] [watch-seconds]
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

ITERS="${1:-8}"
WATCH="${2:-300}"     # how long after the rollout to let the cluster act before capturing

OUT="runs/heal-test"
TSV="$OUT/results.tsv"
mkdir -p "$OUT"
[ -f "$TSV" ] || printf 'iteration\toutcome\thealed\tpersisted\tlongest_heal_s\n' > "$TSV"

# The only question this script asks the store is "how many sim agents are placed"; everything
# else it needs is already in the pod logs. backend.sh answers it on either arm.
source scripts/backend.sh

live_pods() { $K get pods -l app=churnsim -o json 2>/dev/null | python3 scripts/live_pods.py; }
placed()    { db_placed; }

capture() {
    local dir="$1"
    mkdir -p "$dir"
    for p in $(live_pods); do
        $K logs "$p" > "$dir/raw.$p.jsonl" 2>/dev/null
    done
}

wait_settled() {
    local last=-1 stable=0 n
    for _ in $(seq 1 120); do
        n=$(placed)
        if [ "${n:-0}" = "500" ]; then
            if [ "$n" = "$last" ]; then
                stable=$(( stable + 1 ))
                [ "$stable" -ge 3 ] && return 0
            else
                stable=0
            fi
        fi
        last="$n"
        sleep 10
    done
    return 1
}

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
    line=$(python3 scripts/overlaps.py "$OUT/iter$i" --verbose > "$OUT/iter$i/overlaps.txt" 2>&1; head -1 "$OUT/iter$i/overlaps.txt")
    healed=$(sed -n 's/.*overlaps_healed=\([0-9]*\).*/\1/p' <<<"$line")
    persisted=$(sed -n 's/.*overlaps_persisted=\([0-9]*\).*/\1/p' <<<"$line")
    healed=${healed:-0}; persisted=${persisted:-0}

    longest=$(grep -oE 'HEALED .* for [0-9.]+s' "$OUT/iter$i/overlaps.txt" 2>/dev/null \
              | grep -oE '[0-9.]+s$' | tr -d s | sort -rn | head -1)

    if   [ "$persisted" -gt 0 ]; then outcome=PERSISTED
    elif [ "$healed"    -gt 0 ]; then outcome=HEALED
    else                             outcome=never; fi

    printf '%s\t%s\t%s\t%s\t%s\n' "$i" "$outcome" "$healed" "$persisted" "${longest:--}" >> "$TSV"
    tail -1 "$TSV"
    [ "$outcome" = never ] && rm -f "$OUT/iter$i"/raw.*.jsonl || sed -n '2,6p' "$OUT/iter$i/overlaps.txt"
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
