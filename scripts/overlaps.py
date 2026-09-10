#!/usr/bin/env python3
"""Find every window where an agent ran on two pods at once, from captured pod logs.

    overlaps.py <dir-with-raw.*.jsonl> [--grace SECONDS] [--verbose]

Replays AGENT-START / AGENT-STOP per (agent, pod) into residency intervals and reports overlaps
between different pods. An interval with no STOP is treated as open to the end of the capture.

Why this instead of polling the cluster: a node-side reconcile sweep can clear a duplicate within a
few health-check ticks (~6s at ChurnSim's 2s cadence). Any sampler coarser than that will miss the
duplicate entirely and report "never happened", which is indistinguishable from "prevented" — the
exact distinction a convergence fix has to be judged on. The logs already carry every start and
stop with a timestamp, so the whole timeline is recoverable after the fact at full resolution.

Each overlap is classified:

  HEALED     - both copies were running, then one stopped. Convergence works; duration is the
               time the cluster was wrong.
  PERSISTED  - both copies were still running when the capture ended.

Overlaps shorter than --grace (default 2s) are ignored: pod clocks differ, and a handover that
briefly runs both copies while the stop is in flight is the protocol working.
"""

import argparse
import collections
import glob
import json
import os
import re
import sys
from datetime import datetime, timedelta

START = re.compile(r"AGENT-START (\S+)")
STOP = re.compile(r"AGENT-STOP (\S+)")


def parse_ts(value):
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def events(path, pod):
    """(timestamp, kind, agent) for each agent event in one pod's log."""
    out = []
    with open(path) as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            ts = kind = uri = None
            if line.startswith("{"):
                try:
                    r = json.loads(line)
                except ValueError:
                    continue
                msg = r.get("Message") or ""
                uri = (r.get("State") or {}).get("AgentUri")
                ts = parse_ts(r.get("Timestamp"))
                if uri and "AGENT-START" in msg:
                    kind = "start"
                elif uri and "AGENT-STOP" in msg:
                    kind = "stop"
            else:
                m = START.search(line)
                if m:
                    kind, uri = "start", m.group(1)
                else:
                    m = STOP.search(line)
                    if m:
                        kind, uri = "stop", m.group(1)
                if kind:
                    m2 = re.search(r" at (\S+)", line)
                    ts = parse_ts(m2.group(1)) if m2 else None
            if kind and uri and ts:
                out.append((ts, kind, uri, pod))
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("directory")
    ap.add_argument("--grace", type=float, default=2.0)
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    files = sorted(glob.glob(os.path.join(args.directory, "raw.*.jsonl")))
    if not files:
        print(f"no raw.*.jsonl in {args.directory}", file=sys.stderr)
        return 2

    all_events = []
    for path in files:
        pod = os.path.basename(path)[len("raw."):-len(".jsonl")]
        all_events.extend(events(path, pod))
    all_events.sort(key=lambda e: e[0])

    if not all_events:
        print("no agent events found", file=sys.stderr)
        return 2

    capture_end = all_events[-1][0]

    # (agent, pod) -> open start time; closed intervals collected as we go.
    open_at = {}
    residencies = collections.defaultdict(list)   # agent -> [(pod, start, end_or_None)]
    for ts, kind, uri, pod in all_events:
        key = (uri, pod)
        if kind == "start":
            if key not in open_at:
                open_at[key] = ts
        elif key in open_at:
            residencies[uri].append((pod, open_at.pop(key), ts))
    for (uri, pod), start in open_at.items():
        residencies[uri].append((pod, start, None))

    grace = timedelta(seconds=args.grace)
    healed, persisted = [], []
    for uri, spans in residencies.items():
        spans.sort(key=lambda s: s[1])
        for i in range(len(spans)):
            for j in range(i + 1, len(spans)):
                pod_a, sa, ea = spans[i]
                pod_b, sb, eb = spans[j]
                if pod_a == pod_b:
                    continue
                lo = max(sa, sb)
                hi = min(ea or capture_end, eb or capture_end)
                if hi - lo <= grace:
                    continue
                still_open = ea is None and eb is None
                rec = (uri, pod_a, pod_b, lo, hi, (hi - lo).total_seconds())
                (persisted if still_open else healed).append(rec)

    print(f"agents={len(residencies)} overlaps_healed={len(healed)} overlaps_persisted={len(persisted)}")
    if args.verbose:
        for label, group in (("PERSISTED", persisted), ("HEALED", healed)):
            for uri, a, b, lo, hi, dur in sorted(group, key=lambda r: -r[5])[:25]:
                print(f"  {label} {uri} on {a[-5:]}+{b[-5:]} for {dur:.1f}s from {lo:%H:%M:%S}")

    return 1 if persisted else 0


if __name__ == "__main__":
    sys.exit(main())
