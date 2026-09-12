#!/usr/bin/env bash
# One arm of the synthetic-self-guard A/B (reconcile-sweep vs its SIM MUTANT with the guard
# disabled). SIM_STALE_NODE_TIMEOUT_SECONDS is expected to be set aggressively (~4s against the 2s
# heartbeat) so peers eject live nodes' rows and the "own snapshot omits self" window actually
# opens; the new injection log line on both builds counts every occurrence.
#
# Phases: settle -> steady observation -> one rolling deploy -> settle -> tail -> capture.
# Output: runs/synth-guard/<arm>/ with raw pod logs, a running-vs-assigned snapshot, and a
# summary of the counters that matter.
#
#   ./scripts/synth-guard-run.sh sweepnoguard
#   ./scripts/synth-guard-run.sh sweepguard
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

ARM="${1:?usage: synth-guard-run.sh <arm-label>}"
STEADY_SECONDS="${STEADY_SECONDS:-240}"
TAIL_SECONDS="${TAIL_SECONDS:-120}"
OUT="runs/synth-guard/$ARM"
mkdir -p "$OUT"

pgpod()  { $K get pod -l app=pg -o jsonpath='{.items[0].metadata.name}'; }
psql_t() { $K exec "$(pgpod)" -- psql -U postgres -d churnsim -qAt -c "$1" 2>/dev/null; }
placed() { psql_t "select count(*) from wolverine.wolverine_node_assignments where id like 'sim://%';" | tr -d '[:space:]'; }

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

snapshot() {
    local dir="$1"
    mkdir -p "$dir"
    : > "$dir/running.tsv"
    for p in $($K get pods -l app=churnsim -o json 2>/dev/null | python3 scripts/live_pods.py); do
        $K logs "$p" > "$dir/raw.$p.jsonl" 2>/dev/null
        python3 scripts/running_agents.py --pod "$p" < "$dir/raw.$p.jsonl" >> "$dir/running.tsv"
    done
    psql_t "select a.id || chr(9) || n.description
              from wolverine.wolverine_node_assignments a
              join wolverine.wolverine_nodes n on n.id = a.node_id
             where a.id like 'sim://%';" > "$dir/assigned.tsv"
    python3 scripts/orphans.py "$dir/running.tsv" "$dir/assigned.tsv" --verbose > "$dir/report.txt" 2>&1
    head -1 "$dir/report.txt"
}

echo "== [$ARM] verifying pod CONFIG =="
POD=$($K get pods -l app=churnsim -o json | python3 scripts/live_pods.py | head -1)
$K logs "$POD" 2>/dev/null | grep -o "CONFIG StaleNodeTimeout=[^\"]*" | head -1 || echo "  WARNING: no StaleNodeTimeout CONFIG line"
$K logs "$POD" 2>/dev/null | grep -o '"version": *"[^"]*"' | head -1 || true

echo "== [$ARM] waiting for initial settle =="
wait_settled || { echo "never settled"; exit 1; }

# Chaos phase: sustained read lag against ONE victim node, via row-level security.
#
# Three deletion-based attempts failed to open the window (see runs/synth-guard/*-nochaos,
# *-slowchaos, *-fastdelete): the ~1-2ms gap between a tick's heartbeat upsert and its snapshot
# read is unhittable from outside, and holding the row deleted just aborts the tick inside
# ensureLocalNodeRegisteredAsync before the read. RLS simulates the production condition itself —
# reads lag writes — deterministically: the app runs as the non-superuser churn_app (see
# synth-guard-prep.sh), and flipping a SELECT policy hides the victim's node row and assignment
# rows from EVERY read while all writes keep succeeding. The victim's own snapshot then omits it
# on every tick, which is the sustained-lag case the synthetic-self guard exists for. The victim
# is a NON-leader so elections don't confound the arms. Monitoring psql runs as postgres
# (superuser, RLS-exempt) and stays omniscient.
echo "== [$ARM] chaos: RLS read-lag against one non-leader victim, ${STEADY_SECONDS}s =="
VICTIM=$(psql_t "select n.id from wolverine.wolverine_nodes n
                  where n.id not in (select node_id from wolverine.wolverine_node_assignments
                                      where id = 'wolverine://leader/')
                  order by n.node_number limit 1;" | tr -d '[:space:]')
echo "  victim node id: $VICTIM"
# Hide ONLY the victim's NODE row from reads — never its assignment rows. Hiding assignments too
# (first attempt) made the leader see the victim's agents as unowned and stop/reassign them on
# normal ticks: churn, but not the guard's domain (injections stayed 0). With only the node row
# hidden, the victim's own snapshot omits itself while its assignments stay visible — precisely the
# "self missing from its own snapshot, agents still claimed here" state that drives the synthetic
# injection. Writes are all permitted (using/check true), so heartbeats and upserts keep working;
# only SELECT is filtered.
$K exec "$(pgpod)" -- psql -U postgres -d churnsim -qAt -c "
  alter table wolverine.wolverine_nodes enable row level security;
  alter table wolverine.wolverine_nodes force row level security;
  drop policy if exists chaos_nodes_sel on wolverine.wolverine_nodes;
  drop policy if exists chaos_nodes_ins on wolverine.wolverine_nodes;
  drop policy if exists chaos_nodes_upd on wolverine.wolverine_nodes;
  drop policy if exists chaos_nodes_del on wolverine.wolverine_nodes;
  create policy chaos_nodes_sel on wolverine.wolverine_nodes for select using (id <> '$VICTIM');
  create policy chaos_nodes_ins on wolverine.wolverine_nodes for insert with check (true);
  create policy chaos_nodes_upd on wolverine.wolverine_nodes for update using (true) with check (true);
  create policy chaos_nodes_del on wolverine.wolverine_nodes for delete using (true);" 2>&1 | grep -v "^$" | head -3
echo "  RLS read-lag armed for ${STEADY_SECONDS}s"
sleep "$STEADY_SECONDS"
snapshot "$OUT/during"
$K exec "$(pgpod)" -- psql -U postgres -d churnsim -qAt -c "
  alter table wolverine.wolverine_nodes no force row level security;
  alter table wolverine.wolverine_nodes disable row level security;" 2>/dev/null
echo "  RLS read-lag disarmed"
sleep 60   # healing window before judging
snapshot "$OUT/steady"

echo "== [$ARM] rolling deploy =="
./scripts/rollout.sh "$(date +%s)" >/dev/null 2>&1
wait_settled || echo "  WARNING: no settle after rollout"
sleep "$TAIL_SECONDS"
snapshot "$OUT/post"

echo "== [$ARM] summary =="
{
    echo "arm: $ARM"
    echo "captured: $(date -u +%FT%TZ)"
    for phase in during steady post; do
        d="$OUT/$phase"
        [ -d "$d" ] || continue
        inj=$(cat "$d"/raw.*.jsonl 2>/dev/null | grep -c "was missing from its own node snapshot")
        eject=$(cat "$d"/raw.*.jsonl 2>/dev/null | grep -c "re-registered its row after it was deleted")
        stop=$(cat "$d"/raw.*.jsonl 2>/dev/null | grep -c "stopping the local copy")
        start=$(cat "$d"/raw.*.jsonl 2>/dev/null | grep -c "but is not running here; starting it")
        claim=$(cat "$d"/raw.*.jsonl 2>/dev/null | grep -c "restoring this node's claim")
        echo "[$phase] injections=$inj peer_ejections=$eject sweep_stops=$stop sweep_starts=$start sweep_reclaims=$claim"
        echo "[$phase] $(head -1 "$d/report.txt")"
    done
} | tee "$OUT/summary.txt"
