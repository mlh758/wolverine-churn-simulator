#!/usr/bin/env python3
"""Reduce a `safetylab check --json` payload to one TSV row: S5, S7, duplicate count, converge.

A file, not a `python3 -c` string inside a bash function. The inline version died on f-string
quote nesting after the shell had already eaten the escapes — and it died *silently* into an
empty field, which shifted every later column in the results table. Anything with quoting this
fiddly belongs in a file where it can be tested on its own.

Reads the payload on stdin; tolerates junk (a nix shellHook banner, a build notice) before it.
"""

import json
import re
import sys


def main() -> int:
    raw = sys.stdin.read()
    try:
        rows = json.loads(raw[raw.index("["):])
    except Exception as e:  # noqa: BLE001 - any parse failure is the same outcome here
        print("?\t?\t?\t-")
        print(f"verdict_row: could not parse check output: {e}", file=sys.stderr)
        return 1

    by = {r["id"]: r for r in rows}

    def verdict(check: str) -> str:
        r = by.get(check)
        if r is None:
            return "?"
        return "FAIL" if r.get("violations") else "PASS"

    # Distinct agents reported as running on two nodes, not the number of violation intervals —
    # one agent can be reported against several pod pairs in a single run.
    duplicates = set()
    for v in by.get("S5", {}).get("violations", []):
        m = re.search(r"(sim://\S+?) ran on both", v.get("detail", ""))
        if m:
            duplicates.add(m.group(1))

    converged = "-"
    for note in by.get("L1", {}).get("notes", []):
        m = re.search(r"converged ([\d.]+)s", note)
        if m:
            converged = m.group(1)

    print("\t".join([verdict("S5"), verdict("S7"), str(len(duplicates)), converged]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
