#!/usr/bin/env python3
"""Compare what is actually running against what the assignment table says.

    orphans.py running.tsv assigned.tsv [--verbose]

    running.tsv   pod<TAB>agentUri     (one line per agent actually running, per pod)
    assigned.tsv  agentUri<TAB>pod     (one line per assignment row, pod = node description)

Prints one summary line for a results table, then optional detail. Exit 1 if anything diverges.

Three distinct divergences, kept separate because they mean different things:

  duplicated  - one agent running on more than one pod. The user-visible bug: a doubled
                projection daemon. This is the number the duplicate-rate experiment counts.
  orphaned    - running on a pod the table does not assign it to (and not duplicated). An
                orphan runner; the precursor to a duplicate.
  missing     - assigned but not running anywhere. GH-3987's "assigned but not running" wedge.

Deliberately dumb and dependency-free. It is the fallback measurement for when the streaming
capture and the checker pipeline are themselves under suspicion — which, in this rig, they
repeatedly have been.
"""

import argparse
import collections
import sys


def load(path: str, key_first: bool) -> list[tuple[str, str]]:
    rows = []
    with open(path) as f:
        for line in f:
            line = line.rstrip("\n")
            if not line.strip():
                continue
            parts = line.split("\t")
            if len(parts) != 2:
                continue
            a, b = parts
            rows.append((a, b) if key_first else (b, a))
    return rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("running")
    ap.add_argument("assigned")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    # both normalised to (pod, agent)
    running = load(args.running, key_first=True)
    assigned = load(args.assigned, key_first=False)

    by_agent_running = collections.defaultdict(set)
    for pod, agent in running:
        by_agent_running[agent].add(pod)

    assigned_pod = {agent: pod for pod, agent in assigned}

    duplicated = {a: p for a, p in by_agent_running.items() if len(p) > 1}
    orphaned = {
        a: p for a, p in by_agent_running.items()
        if len(p) == 1 and assigned_pod.get(a) not in p
    }
    missing = sorted(a for a in assigned_pod if a not in by_agent_running)

    print(
        f"running={len(running)} assigned={len(assigned)} "
        f"duplicated={len(duplicated)} orphaned={len(orphaned)} missing={len(missing)}"
    )

    if args.verbose:
        for label, group in (("DUPLICATED", duplicated), ("ORPHANED", orphaned)):
            for agent, pods in sorted(group.items()):
                where = ", ".join(sorted(pods))
                says = assigned_pod.get(agent, "<unassigned>")
                print(f"  {label} {agent} running on [{where}] table says [{says}]")
        for agent in missing:
            print(f"  MISSING {agent} assigned to [{assigned_pod[agent]}] but running nowhere")

    return 1 if (duplicated or orphaned or missing) else 0


if __name__ == "__main__":
    sys.exit(main())
