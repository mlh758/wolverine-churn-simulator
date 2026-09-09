#!/usr/bin/env python3
"""Currently-running agents on one pod, from its log on stdin. Prints `pod<TAB>agentUri`.

Replays AGENT-START / AGENT-STOP in order and reports what is left open. This is the measurement
that has proved most robust in this rig: it needs nothing but `kubectl logs`, so it works when the
streaming capture, the checker, or the JSON plumbing does not.

Usage:  kubectl logs <pod> | running_agents.py --pod <pod>
"""

import argparse
import re
import sys

START = re.compile(r"AGENT-START (\S+)")
STOP = re.compile(r"AGENT-STOP (\S+)")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pod", required=True)
    args = ap.parse_args()

    running: set[str] = set()
    for line in sys.stdin:
        m = START.search(line)
        if m:
            running.add(m.group(1))
            continue
        m = STOP.search(line)
        if m:
            running.discard(m.group(1))

    for uri in sorted(running):
        print(f"{args.pod}\t{uri}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
