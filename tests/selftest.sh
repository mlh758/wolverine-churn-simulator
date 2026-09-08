#!/usr/bin/env bash
# The checkers' mutant ledger.
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
)

BIN="${SAFETYLAB_BIN:-}"
if [ -z "$BIN" ]; then
  out=$(mktemp -d)
  trap 'rm -rf "$out"' EXIT
  dotnet build src/SafetyLab -c Release -v q --nologo -o "$out" >/dev/null || exit 2
  BIN="dotnet $out/safetylab.dll"
fi

python3 tests/make_fixtures.py >/dev/null || exit 2

failures=0
for row in "${LEDGER[@]}"; do
  fixture="${row%%:*}"
  expected="${row#*:}"

  actual=$($BIN check "tests/fixtures/$fixture" --json 2>/dev/null \
    | jq -r '.[] | select((.violations | length) > 0) | .id' | sort | tr '\n' ' ' | sed 's/ $//')
  expected=$(echo "$expected" | tr ' ' '\n' | sort | tr '\n' ' ' | sed 's/^ *//; s/ *$//')

  if [ "$actual" = "$expected" ]; then
    printf '  ok   %-14s -> [%s]\n' "$fixture" "$actual"
  else
    printf '  FAIL %-14s -> expected [%s], got [%s]\n' "$fixture" "$expected" "$actual"
    failures=$((failures + 1))
  fi
done

echo
if [ "$failures" -eq 0 ]; then
  echo "ledger holds: every checker fires on its own fault and stays quiet on the others"
  exit 0
fi

echo "$failures ledger row(s) failed"
exit 1
