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
  # The RavenDB arm. raven-clean pins that the leader-side checkers read compare-exchange
  # evidence rather than only pg_locks -- without it they would pass a RavenDB run vacuously,
  # which is the same way the first live PostgreSQL run passed every leader check.
  "raven-clean:"
  "raven-expired-lock:S8 L1"
  "raven-split-key:S1 L1"
  "raven-truncated:C0"
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
