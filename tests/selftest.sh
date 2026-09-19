#!/usr/bin/env bash
# The checkers' mutant ledger, plus the cluster-decision unit tests.
#
# Each fixture injects exactly one fault and declares which checks must fail. Both halves
# matter: a check that never fires is decoration, and a check that fires on everything is
# noise nobody will read twice. If you add a checker, add a fixture and a row here — a run
# reporting "0 violations" is only worth as much as the evidence that these can go red.
#
#   ./tests/selftest.sh          (inside `nix develop`, or it will find the SDK itself)
set -uo pipefail
cd "$(dirname "$0")/.."

# fixture:expected-failing-check-ids (space separated, empty = must be wholly clean)
LEDGER=(
  "clean:"
  "dup-agent:S5 S7"
  "orphan-lock:S3 S4 L1"
  "stranded:S6"
  "no-converge:L1"
  "split-leader:S3"
  "gap:C0"
  # E8, the lock-session kill. `lock-kill` is the fault landing: the leader row outlives the lock
  # by 20s, which is S3. `lock-kill-noop` has the mark and a lock that never moved -- the nemesis
  # did not fire, every other check passes over an undisturbed cluster, and K1 is the only thing
  # that can tell the two runs apart.
  "lock-kill:S3"
  "lock-kill-noop:K1"
  # The control arm asserts the OPPOSITE: `lock-cancel` is the same undisturbed cluster as
  # lock-kill-noop under the other mark and must be wholly clean, while `lock-cancel-moved` is a
  # lock that left after a mere cancel -- the client dropped its connection, which is a finding
  # and not a control. Without this pair K1 failed every correct control run.
  "lock-cancel:"
  "lock-cancel-moved:S3 K1"
  # E9, the app-to-store cut. `db-cut` is the leader losing the database while its session -- and
  # so its lock -- stays alive on the server: the assignment table reads as a perfectly healthy
  # fully-placed cluster throughout, and S11 is the only check that refuses it. `db-cut-noop` is
  # the cut that never took, which only K2 can distinguish from a quiet run.
  "db-cut:S11"
  "db-cut-noop:K2"
  # The RavenDB arm. raven-clean pins that the leader-side checkers read compare-exchange
  # evidence rather than only pg_locks -- without it they would pass a RavenDB run vacuously,
  # which is the same way the first live PostgreSQL run passed every leader check.
  "raven-clean:"
  "raven-expired-lock:S8 L1"
  "raven-split-key:S1 L1"
  "raven-truncated:C0"
  # The replicated store (E7). raven-replicas-clean pins that three members agreeing on one lock
  # read as ONE holder and that S9/S10 stay quiet on a healthy cluster. raven-partition is the
  # predicted split: two members naming two lock owners (S1) and a minority member holding
  # assignment rows the majority does not (S9). raven-partition-noop has the marks of a partition
  # run and no member that ever lost its Raft leader -- the cut did not take, and P1 refuses it.
  "raven-replicas-clean:"
  "raven-partition:S1 S9"
  "raven-conflicts:S10"
  "raven-partition-noop:P1"
)

BIN="${SAFETYLAB_BIN:-}"
if [ -z "$BIN" ]; then
  out=$(mktemp -d)
  trap 'rm -rf "$out"' EXIT
  dotnet build src/SafetyLab -c Release -v q --nologo -o "$out" >/dev/null || exit 2
  BIN="dotnet $out/safetylab.dll"
fi

python3 tests/make_fixtures.py >/dev/null || exit 2

# The cluster-decision layer (pod liveness, host-pid resolution, leader state, nemesis validity).
# Pure functions over captured fixtures -- no cluster, ~30ms. Every one of these is a regression
# test for a defect that actually shipped into a measurement; see tests/SafetyLab.Tests.
echo "cluster-decision tests:"
if ! dotnet test tests/SafetyLab.Tests -v q --nologo 2>&1 | grep -E "^(Passed!|Failed!)" | sed 's/^/  /'; then
  echo "  FAIL: dotnet test did not report a result" >&2
  exit 1
fi
echo
echo "checker ledger:"

failures=0
checks=0

for row in "${LEDGER[@]}"; do
  fixture="${row%%:*}"
  expected="${row#*:}"

  # Keep the payload. Reading the ids straight out of a pipe meant a checker that CRASHED produced
  # no output, `actual` came back empty, and empty is exactly what the `clean:` rows expect -- so a
  # broken binary passed the two fixtures whose whole job is to prove the checkers stay quiet. The
  # ledger only means something if the checkers ran, so that is asserted first.
  json=$($BIN check "tests/fixtures/$fixture" --json 2>/dev/null)
  n=$(jq 'length' <<<"$json" 2>/dev/null)

  if [ -z "$n" ] || [ "$n" -eq 0 ] 2>/dev/null; then
    printf '  FAIL %-18s -> the checker produced no results at all (crashed, or wrote no JSON)\n' "$fixture"
    failures=$((failures + 1))
    continue
  fi

  # And that the SAME checkers ran on every fixture. One silently dropping out would otherwise
  # look like a fixture that stopped triggering it.
  if [ "$checks" -eq 0 ]; then
    checks="$n"
  elif [ "$n" -ne "$checks" ]; then
    printf '  FAIL %-18s -> ran %s checks; every other fixture ran %s\n' "$fixture" "$n" "$checks"
    failures=$((failures + 1))
    continue
  fi

  actual=$(jq -r '.[] | select((.violations | length) > 0) | .id' <<<"$json" \
    | sort | tr '\n' ' ' | sed 's/ $//')
  expected=$(echo "$expected" | tr ' ' '\n' | sort | tr '\n' ' ' | sed 's/^ *//; s/ *$//')

  if [ "$actual" = "$expected" ]; then
    printf '  ok   %-18s -> [%s]\n' "$fixture" "$actual"
  else
    printf '  FAIL %-18s -> expected [%s], got [%s]\n' "$fixture" "$expected" "$actual"
    failures=$((failures + 1))
  fi
done

echo
if [ "$failures" -eq 0 ]; then
  echo "ledger holds: all $checks checks ran on every fixture, each firing on its own fault"
  exit 0
fi

echo "$failures ledger row(s) failed"
exit 1
