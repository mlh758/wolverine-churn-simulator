#!/usr/bin/env bash
# How often does a rolling deploy leave an agent running on two nodes at once?
#
# One never-healed duplicate was confirmed on stock main 6.35.0 (RESULTS.md 2026-09-09) and a
# second reproduced immediately afterwards. This counts them.
#
# DELIBERATELY SIMPLE. An earlier version switched the GH-4367 settle gate between interleaved
# arms and measured through the full SafetyLab capture pipeline. It spent three attempts failing
# on its own machinery rather than on Wolverine: a set-env/rollout-restart race that mislabelled
# arms, terminating pods sampled as live, and a JSON parser that failed into an empty column.
# None of that was measuring anything.
#
# So: one arm, and the one measurement that has never let us down -- agents actually running
# (from `kubectl logs`) versus agents assigned (from the table). No streaming capture, no JSON.
# The duplicate that started all this was found exactly this way, and so was its first repeat.
#
# The gate arm is dropped on purpose: the bug has now been seen with the gate both ON and OFF,
# and 8 runs per arm could never have separated them anyway (noted when the arms were designed).
#
#   ./scripts/duplicate-rate.sh [iterations]      # default 12
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

ITERS="${1:-12}"
# A settle at or above this keeps its raw logs even when the iteration is otherwise clean.
SLOW_SETTLE="${SLOW_SETTLE:-120}"
OUT="runs/duplicate-rate"
TSV="$OUT/results.tsv"
mkdir -p "$OUT"
[ -f "$TSV" ] || printf 'iteration\tresult\trunning\tassigned\tduplicated\torphaned\tmissing\tsettle_s\n' > "$TSV"

# Two questions of the store -- what is placed, and who owns what -- answered on either arm by
# scripts/backend.sh. The running side never touches the store at all: it is replayed from the pod
# logs, which is the whole reason this measurement survived when the streaming pipeline did not.
source scripts/backend.sh

live_pods() { $K get pods -l app=churnsim -o json 2>/dev/null | python3 scripts/live_pods.py; }
placed()    { db_placed; }

# Snapshot both sides into a directory and diff them.
#
# The raw pod log is KEPT, not piped away. A duplicate is detected after the fact and its pods are
# replaced by the next iteration, so without this the evidence for *why* it happened is gone by the
# time anyone looks. Same single `kubectl logs` fetch either way: write it down, then derive the
# running set from the file. The snapshot directory is then directly queryable --
# `scripts/logq.sh runs/duplicate-rate/iterN/post "<sql>"` globs exactly these raw.*.jsonl files.
snapshot() {
    local dir="$1"
    mkdir -p "$dir"
    : > "$dir/running.tsv"
    for p in $(live_pods); do
        $K logs "$p" > "$dir/raw.$p.jsonl" 2>/dev/null
        python3 scripts/running_agents.py --pod "$p" < "$dir/raw.$p.jsonl" >> "$dir/running.tsv"
    done
    db_assigned > "$dir/assigned.tsv"
    python3 scripts/orphans.py "$dir/running.tsv" "$dir/assigned.tsv" --verbose > "$dir/report.txt" 2>&1
    head -1 "$dir/report.txt"
}

# Wait for full placement, then for it to hold still for three consecutive polls.
#
# Records the placement count at every poll to $2 when given. Two runs have now produced an
# iteration that took *exactly* 831 s to settle against 30-51 s for every other iteration, on
# independent clusters -- a deterministic stall, not a slow tail. The count alone cannot say
# whether placement is stuck flat and then jumps (a timeout expiring, e.g. the 10-minute
# AgentReleaseCooldown) or crawls (batch pacing). The series can.
wait_settled() {
    local start last=-1 stable=0 n series="${2:-/dev/null}"
    start=$(date +%s)
    : > "$series"
    for _ in $(seq 1 120); do
        n=$(placed)
        printf '%s\t%s\n' "$(( $(date +%s) - start ))" "${n:-}" >> "$series"
        if [ "${n:-0}" = "500" ]; then
            if [ "$n" = "$last" ]; then
                stable=$(( stable + 1 ))
                if [ "$stable" -ge 3 ]; then echo $(( $(date +%s) - start )); return 0; fi
            else
                stable=0
            fi
        fi
        last="$n"
        sleep 10
    done
    echo -1
    return 1
}

field() { sed -n "s/.*$1=\([0-9]*\).*/\1/p" <<<"$2"; }

gate=$($K get deployment churnsim \
        -o jsonpath='{range .spec.template.spec.containers[0].env[?(@.name=="SIM_STABILITY_WINDOW_SECONDS")]}{.value}{end}')
echo "duplicate-rate: $ITERS rollouts, single arm"
echo "backend: $(sim_backend)"
echo "settle gate (GH-4367): $( [ -n "$gate" ] && echo "ON ($gate s)" || echo OFF )"

for i in $(seq 1 "$ITERS"); do
    echo "== iteration $i =="

    # Fresh pods, so nothing from the previous iteration is still running.
    $K rollout restart deployment/churnsim >/dev/null 2>&1
    $K rollout status deployment/churnsim --timeout=900s >/dev/null 2>&1
    if ! wait_settled >/dev/null; then
        printf '%s\tSKIP-no-settle\t-\t-\t-\t-\t-\t-\n' "$i" >> "$TSV"; tail -1 "$TSV"; continue
    fi
    sleep 45

    # A dirty start cannot measure this rollout. Recorded as a skip, never as a pass -- and the
    # snapshot is kept, because a dirty start is itself a duplicate from the previous rollout.
    pre=$(snapshot "$OUT/iter$i/pre")
    if ! grep -q "duplicated=0 orphaned=0 missing=0" <<<"$pre"; then
        printf '%s\tSKIP-dirty-start\t-\t-\t-\t-\t-\t-\n' "$i" >> "$TSV"
        echo "  dirty start: $pre"
        echo "     evidence kept: $OUT/iter$i/pre/raw.*.jsonl"
        tail -1 "$TSV"; continue
    fi

    # The measured event.
    mkdir -p "$OUT/iter$i"
    ./scripts/rollout.sh "$(date +%s)" >/dev/null 2>&1
    settle=$(wait_settled "" "$OUT/iter$i/placement.tsv")
    sleep 120   # let any late reconcile happen before judging

    post=$(snapshot "$OUT/iter$i/post")
    dup=$(field duplicated "$post"); orp=$(field orphaned "$post"); mis=$(field missing "$post")
    run=$(field running "$post");    asg=$(field assigned "$post")

    if [ "${dup:-0}" -gt 0 ]; then result=DUPLICATE
    elif [ "${orp:-0}" -gt 0 ] || [ "${mis:-0}" -gt 0 ]; then result=DIVERGED
    else result=clean; fi

    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$i" "$result" "$run" "$asg" "$dup" "$orp" "$mis" "$settle" >> "$TSV"
    tail -1 "$TSV"

    if [ "$result" = clean ] && [ "${settle:-0}" -lt "$SLOW_SETTLE" ]; then
        # Nothing to explain: reclaim the raw logs, keep running/assigned/report as the record.
        rm -f "$OUT/iter$i"/*/raw.*.jsonl
    elif [ "$result" = clean ]; then
        echo "     SLOW SETTLE ${settle}s (>= ${SLOW_SETTLE}s) — evidence kept: $OUT/iter$i/"
    else
        sed -n '2,8p' "$OUT/iter$i/post/report.txt"
        echo "     evidence kept: $OUT/iter$i/{pre,post}/raw.*.jsonl"
    fi
done

echo
echo "======== SUMMARY ========"
python3 - "$TSV" <<'PY'
import sys, collections
rows = [l.rstrip("\n").split("\t") for l in open(sys.argv[1])][1:]
c = collections.Counter(r[1] for r in rows if len(r) > 1)
measured = sum(v for k, v in c.items() if not k.startswith("SKIP"))
dup, div = c.get("DUPLICATE", 0), c.get("DIVERGED", 0)
print(f"  measured rollouts : {measured}")
print(f"  duplicate agents  : {dup}" + (f"   ({100*dup/measured:.0f}% of measured)" if measured else ""))
print(f"  other divergence  : {div}")
print(f"  clean             : {c.get('clean', 0)}")
for k, v in sorted(c.items()):
    if k.startswith("SKIP"):
        print(f"  {k:<18}: {v}")
settles = [float(r[7]) for r in rows if len(r) > 7 and r[7] not in ("-", "-1")]
if settles:
    settles.sort()
    print(f"  settle seconds    : median {settles[len(settles)//2]:.0f}  max {settles[-1]:.0f}")
PY
echo "DUPLICATE RATE EXPERIMENT COMPLETE"
