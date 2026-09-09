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
OUT="runs/duplicate-rate"
TSV="$OUT/results.tsv"
mkdir -p "$OUT"
[ -f "$TSV" ] || printf 'iteration\tresult\trunning\tassigned\tduplicated\torphaned\tmissing\tsettle_s\n' > "$TSV"

live_pods() { $K get pods -l app=churnsim -o json 2>/dev/null | python3 scripts/live_pods.py; }
pgpod()     { $K get pod -l app=pg -o jsonpath='{.items[0].metadata.name}'; }
psql_t()    { $K exec "$(pgpod)" -- psql -U postgres -d churnsim -qAt -c "$1" 2>/dev/null; }
placed()    { psql_t "select count(*) from wolverine.wolverine_node_assignments where id like 'sim://%';" | tr -d '[:space:]'; }

# Snapshot both sides into a directory and diff them.
snapshot() {
    local dir="$1"
    mkdir -p "$dir"
    : > "$dir/running.tsv"
    for p in $(live_pods); do
        $K logs "$p" 2>/dev/null | python3 scripts/running_agents.py --pod "$p" >> "$dir/running.tsv"
    done
    psql_t "select a.id || chr(9) || n.description
              from wolverine.wolverine_node_assignments a
              join wolverine.wolverine_nodes n on n.id = a.node_id
             where a.id like 'sim://%';" > "$dir/assigned.tsv"
    python3 scripts/orphans.py "$dir/running.tsv" "$dir/assigned.tsv" --verbose > "$dir/report.txt" 2>&1
    head -1 "$dir/report.txt"
}

# Wait for full placement, then for it to hold still for three consecutive polls.
wait_settled() {
    local start last=-1 stable=0 n
    start=$(date +%s)
    for _ in $(seq 1 120); do
        n=$(placed)
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
        echo "  dirty start: $pre"; tail -1 "$TSV"; continue
    fi

    # The measured event.
    ./scripts/rollout.sh "$(date +%s)" >/dev/null 2>&1
    settle=$(wait_settled)
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
    if [ "$result" != clean ]; then sed -n '2,8p' "$OUT/iter$i/post/report.txt"; fi
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
