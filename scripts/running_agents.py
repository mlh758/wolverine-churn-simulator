#!/usr/bin/env python3
"""Currently-running agents on one pod, from its log on stdin. Prints `pod<TAB>agentUri`.

Replays AGENT-START / AGENT-STOP in order and reports what is left open. This is the measurement
that has proved most robust in this rig: it needs nothing but `kubectl logs`, so it works when the
streaming capture, the checker, or the JSON plumbing does not.

Usage:  kubectl logs <pod> | running_agents.py --pod <pod>
"""

import argparse
import json
import re
import sys

START = re.compile(r"AGENT-START (\S+)")
STOP = re.compile(r"AGENT-STOP (\S+)")


def agent_event(line: str):
    """(event, uri) for a log line, or None.

    Prefers the structured form: with SIM_JSON_LOGS=true each line is a JSON object whose State
    holds the message-template parameters, so AgentUri is a named field rather than something to
    pattern-match out of prose. Falls back to the text format so captures taken before the JSON
    formatter existed still parse.
    """
    line = line.strip()
    if line.startswith("{"):
        try:
            record = json.loads(line)
        except ValueError:
            return None
        message = record.get("Message") or ""
        uri = (record.get("State") or {}).get("AgentUri")
        if uri:
            if "AGENT-START" in message:
                return "start", uri
            if "AGENT-STOP" in message:
                return "stop", uri
        return None

    m = START.search(line)
    if m:
        return "start", m.group(1)
    m = STOP.search(line)
    if m:
        return "stop", m.group(1)
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pod", required=True)
    args = ap.parse_args()

    running: set[str] = set()
    for line in sys.stdin:
        event = agent_event(line)
        if event is None:
            continue
        kind, uri = event
        if kind == "start":
            running.add(uri)
        else:
            running.discard(uri)

    for uri in sorted(running):
        print(f"{args.pod}\t{uri}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
