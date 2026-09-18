#!/usr/bin/env bash
# How often does a rolling deploy leave an agent running on two nodes at once?
#
# DEPENDS ON  a deployed churnsim cluster (either arm), $SAFETYLAB, scripts/rollout.sh.
#             The RavenDB arm additionally needs ./scripts/monitor.sh deploy.
# REQUIRES    the cluster settled, and $TSV either absent or already holding this backend's rows.
# PRODUCES    a row per iteration appended to $OUT/results.tsv:
#               iteration backend result running assigned duplicated orphaned missing settle_s
#             result is clean | DUPLICATE | DIVERGED | SKIP-no-settle | SKIP-dirty-start |
#             SKIP-unmeasurable. Raw pod logs are kept for every iteration that is not clean-and-
#             fast, under $OUT/iterN/{pre,post}/.
#
# One arm only. The gate arm was dropped on purpose: the bug has been seen with the GH-4367 settle
# gate both ON and OFF, and 8 runs per arm could never have separated them.
#
# ARGUMENTS
#
#   ./scripts/duplicate-rate.sh [iterations]   default 12
#   OUT=<dir>                                  results directory; default runs/duplicate-rate
#   SLOW_SETTLE=<seconds>                      a settle at or above this keeps its raw logs even
#                                              when the iteration is otherwise clean; default 120
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

ITERS="${1:-12}"
# A settle at or above this keeps its raw logs even when the iteration is otherwise clean.
SLOW_SETTLE="${SLOW_SETTLE:-120}"

# Overridable, because there are now two arms and a rate is meaningless pooled across stores.
# Every row carries its backend, and the script refuses to append to a file written under a
# different one -- the summary at the bottom divides by the row count, so one stray arm's rows
# silently move the headline number. Files written before the column existed are PostgreSQL.
OUT="${OUT:-runs/duplicate-rate}"
TSV="$OUT/results.tsv"
HEADER=$(printf 'iteration\tbackend\tresult\trunning\tassigned\tduplicated\torphaned\tmissing\tsettle_s')
mkdir -p "$OUT"

# The running side never touches the store: it is replayed from the pod logs, which is the whole
# reason this measurement survived when the streaming pipeline did not.
source scripts/backend.sh
require_safetylab

# `safetylab snapshot` prints the results.tsv columns
#
#     verdict <TAB> running <TAB> assigned <TAB> duplicated <TAB> orphaned <TAB> missing
#
# and exits 0 clean / 1 diverged / 2 COULD NOT MEASURE. The raw pod log is KEPT: a duplicate is
# detected after the fact and its pods are replaced by the next iteration, so the evidence for
# *why* is gone by the time anyone looks. `scripts/logq.sh <dir> "<sql>"` globs those files.
snapshot() { "$SAFETYLAB" snapshot "$1" --tsv; }

# `safetylab settle`: exit 0 settled (elapsed seconds on stdout), 1 never settled (-1), 2 could
# not measure. The target is the deployment's SIM_AGENT_COUNT, not a literal written into the loop.
wait_settled() { "$SAFETYLAB" settle ${1:+--series "$1"}; }

gate=$($K get deployment churnsim \
        -o jsonpath='{range .spec.template.spec.containers[0].env[?(@.name=="SIM_STABILITY_WINDOW_SECONDS")]}{.value}{end}')
BACKEND=$(sim_backend)

if [ -f "$TSV" ]; then
    existing=$(head -1 "$TSV")
    if [ "$existing" != "$HEADER" ]; then
        echo "$TSV was written before rows carried a backend, so its arm cannot be verified." >&2
        echo "    Those rows are PostgreSQL. Run this arm somewhere else:" >&2
        echo "      OUT=runs/duplicate-rate-$BACKEND ./scripts/duplicate-rate.sh $ITERS" >&2
        exit 2
    fi
    if [ -n "$(awk -F'\t' -v b="$BACKEND" 'NR>1 && $2 != "" && $2 != b { print; exit }' "$TSV")" ]; then
        echo "$TSV already holds rows from a different backend. A duplicate rate pooled across" >&2
        echo "    stores is not a result. Use OUT=runs/duplicate-rate-$BACKEND instead." >&2
        exit 2
    fi
else
    printf '%s\n' "$HEADER" > "$TSV"
fi

echo "duplicate-rate: $ITERS rollouts, single arm"
echo "backend: $BACKEND"
echo "settle gate (GH-4367): $( [ -n "$gate" ] && echo "ON ($gate s)" || echo OFF )"

for i in $(seq 1 "$ITERS"); do
    echo "== iteration $i =="

    # Fresh pods, so nothing from the previous iteration is still running.
    $K rollout restart deployment/churnsim >/dev/null 2>&1
    $K rollout status deployment/churnsim --timeout=900s >/dev/null 2>&1
    if ! wait_settled >/dev/null; then
        printf '%s\t%s\tSKIP-no-settle\t-\t-\t-\t-\t-\t-\n' "$i" "$BACKEND" >> "$TSV"; tail -1 "$TSV"; continue
    fi
    sleep 45

    # A dirty start cannot measure this rollout. Recorded as a skip, never as a pass -- and the
    # snapshot is kept, because a dirty start is itself a duplicate from the previous rollout.
    # "Already dirty" and "could not see the cluster" stay separate outcomes.
    snapshot "$OUT/iter$i/pre" >/dev/null; rc=$?
    case "$rc" in
        0) ;;
        1)  printf '%s\t%s\tSKIP-dirty-start\t-\t-\t-\t-\t-\t-\n' "$i" "$BACKEND" >> "$TSV"
            echo "  dirty start: $(head -1 "$OUT/iter$i/pre/report.txt")"
            echo "     evidence kept: $OUT/iter$i/pre/raw.*.jsonl"
            tail -1 "$TSV"; continue ;;
        *)  printf '%s\t%s\tSKIP-unmeasurable\t-\t-\t-\t-\t-\t-\n' "$i" "$BACKEND" >> "$TSV"
            echo "  the pre-rollout snapshot was refused; this iteration measures nothing"
            tail -1 "$TSV"; continue ;;
    esac

    # The measured event.
    mkdir -p "$OUT/iter$i"
    ./scripts/rollout.sh "$(date +%s)" >/dev/null 2>&1
    # Check the status: an iteration that never converged must not be judged as if it had.
    settle=$(wait_settled "$OUT/iter$i/placement.tsv"); settled=$?
    if [ "$settled" -ge 2 ]; then
        printf '%s\t%s\tSKIP-unmeasurable\t-\t-\t-\t-\t-\t-\n' "$i" "$BACKEND" >> "$TSV"
        echo "  the store could not be read while waiting to settle"
        tail -1 "$TSV"; continue
    fi
    if [ "$settled" -ne 0 ]; then
        printf '%s\t%s\tSKIP-no-settle\t-\t-\t-\t-\t-\t%s\n' "$i" "$BACKEND" "$settle" >> "$TSV"
        echo "  never settled after the rollout; not judging a cluster that has not converged"
        echo "     evidence kept: $OUT/iter$i/"
        tail -1 "$TSV"; continue
    fi

    sleep 120   # let any late reconcile happen before judging

    # $post is already the six results.tsv columns. Nothing here parses it; the exit code branches.
    post=$(snapshot "$OUT/iter$i/post"); rc=$?

    if [ "$rc" -ge 2 ]; then
        printf '%s\t%s\tSKIP-unmeasurable\t-\t-\t-\t-\t-\t%s\n' "$i" "$BACKEND" "$settle" >> "$TSV"
        echo "     evidence kept: $OUT/iter$i/"
        tail -1 "$TSV"; continue
    fi

    printf '%s\t%s\t%s\t%s\n' "$i" "$BACKEND" "$post" "$settle" >> "$TSV"
    tail -1 "$TSV"

    if [ "$rc" -eq 0 ] && [ "${settle:-0}" -lt "$SLOW_SETTLE" ]; then
        # Nothing to explain: reclaim the raw logs, keep running/assigned/report as the record.
        rm -f "$OUT/iter$i"/*/raw.*.jsonl
    elif [ "$rc" -eq 0 ]; then
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
c = collections.Counter(r[2] for r in rows if len(r) > 2)
measured = sum(v for k, v in c.items() if not k.startswith("SKIP"))
dup, div = c.get("DUPLICATE", 0), c.get("DIVERGED", 0)
print(f"  backend           : {', '.join(sorted({r[1] for r in rows if len(r) > 1}))}")
print(f"  measured rollouts : {measured}")
print(f"  duplicate agents  : {dup}" + (f"   ({100*dup/measured:.0f}% of measured)" if measured else ""))
print(f"  other divergence  : {div}")
print(f"  clean             : {c.get('clean', 0)}")
for k, v in sorted(c.items()):
    if k.startswith("SKIP"):
        print(f"  {k:<18}: {v}")
settles = [float(r[8]) for r in rows if len(r) > 8 and r[8] not in ("-", "-1")]
if settles:
    settles.sort()
    print(f"  settle seconds    : median {settles[len(settles)//2]:.0f}  max {settles[-1]:.0f}")
PY
echo "DUPLICATE RATE EXPERIMENT COMPLETE"
