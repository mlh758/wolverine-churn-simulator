#!/usr/bin/env python3
"""Generate synthetic run directories with known-bad histories.

A checker that has never been seen to fire is not evidence of anything. These fixtures are
the checkers' mutant ledger: each one injects exactly one fault, and selftest.sh asserts that
the matching check fails and — just as importantly — that the others do not. Every fixture is
also a written statement of what the corresponding real-world bug looks like from outside the
cluster, which is the part that is easy to lose.

Regenerate with:  python3 tests/make_fixtures.py
"""

import json
import os
import shutil
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.join(HERE, "fixtures")

T0 = datetime(2026, 9, 8, 12, 0, 0, tzinfo=timezone.utc)
TICK = timedelta(seconds=1)
DURATION = 240  # samples, i.e. four minutes

LEADER_LOCK = 9999999
LEADER_URI = "wolverine://leader"

NODES = [
    # (pod name, pod ip, node uuid, node number, backend pid)
    ("churnsim-a", "10.0.0.11", "11111111-1111-1111-1111-111111111111", 1, 101),
    ("churnsim-b", "10.0.0.12", "22222222-2222-2222-2222-222222222222", 2, 102),
    ("churnsim-c", "10.0.0.13", "33333333-3333-3333-3333-333333333333", 3, 103),
]

AGENTS = [f"sim://agent{i}" for i in range(1, 7)]


def iso(t):
    return t.isoformat().replace("+00:00", "Z")


class World:
    """The cluster state a fixture wants at a given tick, before any fault is applied."""

    def __init__(self):
        # Node A leads; agents distributed round-robin over all three.
        self.live = {n[2] for n in NODES}
        self.leader = NODES[0][2]
        self.lock_holder = NODES[0]  # the tuple, so we keep pid + ip
        self.placement = {a: NODES[i % 3][2] for i, a in enumerate(AGENTS)}
        self.running = {a: NODES[i % 3][0] for i, a in enumerate(AGENTS)}  # agent -> pod


def sample(seq, t, w):
    locks = []
    if w.lock_holder is not None:
        locks.append({
            "pid": w.lock_holder[4],
            "classId": 0,
            "objId": LEADER_LOCK,
            "objSubId": 1,
            "granted": True,
            "applicationName": "wolverine-advisory-lock:churnsim",
            "clientAddr": w.lock_holder[1],
            "backendState": "idle",
            "backendStart": iso(T0 - timedelta(seconds=30)),
        })

    nodes = [
        {"id": n[2], "nodeNumber": n[3], "healthCheck": iso(t), "description": n[0]}
        for n in NODES if n[2] in w.live
    ]

    assignments = []
    if w.leader is not None:
        assignments.append({"id": LEADER_URI, "nodeId": w.leader, "started": iso(T0)})
    for agent, node in sorted(w.placement.items()):
        assignments.append({"id": agent, "nodeId": node, "started": iso(T0)})

    return {
        "kind": "sample",
        "seq": seq,
        "ts": iso(t),
        "dbTs": iso(t + timedelta(milliseconds=3)),
        "elapsedMs": 4.2,
        "locks": locks,
        "nodes": nodes,
        "assignments": assignments,
    }


def build(name, mutate=None, skip=None):
    """mutate(tick, world) adjusts state in place; skip(tick) drops a sample entirely."""
    directory = os.path.join(FIXTURES, name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(directory)

    meta = {
        "kind": "meta",
        "schemaVersion": 1,
        "startedUtc": iso(T0),
        "tickMs": 1000,
        "leaderLockId": LEADER_LOCK,
        "schema": "wolverine",
        "serverVersion": "PostgreSQL 16.4 (fixture)",
    }

    history = [meta]
    for tick in range(DURATION):
        if skip and skip(tick):
            continue
        t = T0 + tick * TICK
        w = World()
        if mutate:
            mutate(tick, w)
        history.append(sample(tick + 1, t, w))

    # Pod-side stream. Residencies are derived from the same World the samples use, so a
    # fixture only has to state its fault once.
    pods = []
    for pod, ip, node_id, _, _ in NODES:
        pods.append({"kind": "identity", "ts": iso(T0), "nodeId": node_id,
                     "podName": pod, "podIp": ip})

    previous = {}
    for tick in range(DURATION):
        t = T0 + tick * TICK
        w = World()
        if mutate:
            mutate(tick, w)
        current = {(agent, pod) for agent, pod in w.running.items()}

        for pair in current - set(previous):
            pods.append({"kind": "agent", "ts": iso(t), "event": "start",
                         "agentUri": pair[0], "podName": pair[1]})
        for pair in set(previous) - current:
            pods.append({"kind": "agent", "ts": iso(t), "event": "stop",
                         "agentUri": pair[0], "podName": pair[1]})
        previous = current

    marks = [{"kind": "mark", "ts": iso(T0 + timedelta(seconds=90)), "label": "rollout-end"}]

    write(os.path.join(directory, "history.jsonl"), history)
    write(os.path.join(directory, "pods.jsonl"), sorted(pods, key=lambda r: r["ts"]))
    write(os.path.join(directory, "marks.jsonl"), marks)
    print(f"  {name}")


def write(path, records):
    with open(path, "w") as f:
        for record in records:
            f.write(json.dumps(record) + "\n")


# ---------------------------------------------------------------- the fixtures

def clean(tick, w):
    pass


def orphan_lock(tick, w):
    """The stacking bug: node A leaves, but its session still holds the advisory lock.

    Its row is gone from wolverine_nodes and the leader assignment row went with it (the FK
    cascades), yet pg_locks still shows the lock held by A's backend. No survivor can win an
    election while that holds, and nothing anywhere logs it.
    """
    if tick >= 120:
        a = NODES[0][2]
        w.live.discard(a)
        w.leader = None
        w.placement = {k: v for k, v in w.placement.items() if v != a}
        w.running = {k: v for k, v in w.running.items() if v != NODES[0][0]}
        # w.lock_holder deliberately left as node A


def stranded(tick, w):
    """GH-3987: the row says node C owns agent3, but pod-c never started it."""
    if tick >= 60:
        w.running = {k: v for k, v in w.running.items() if k != "sim://agent3"}


def no_converge(tick, w):
    """Two agents are never placed after the rollout — the assignment plane gave up."""
    if tick >= 60:
        w.placement = {k: v for k, v in w.placement.items()
                       if k not in ("sim://agent5", "sim://agent6")}
        w.running = {k: v for k, v in w.running.items()
                     if k not in ("sim://agent5", "sim://agent6")}


def split_leader(tick, w):
    """The leader row names node A while the advisory lock is held by node B."""
    if tick >= 120:
        w.lock_holder = NODES[1]


def main():
    os.makedirs(FIXTURES, exist_ok=True)
    print("fixtures:")
    build("clean", clean)
    build("orphan-lock", orphan_lock)
    build("stranded", stranded)
    build("no-converge", no_converge)
    build("split-leader", split_leader)
    build("gap", clean, skip=lambda tick: 150 <= tick < 180)
    build_dup()


def build_dup():
    """Built separately: it is the one fixture whose fault lives in the pod stream only.

    The assignment table keeps saying agent1 belongs to node A the whole time. Only the
    AGENT-START/STOP logs show pod-b running it too — which is exactly why S5 reads the pod
    stream rather than trusting the table.
    """
    name = "dup-agent"
    directory = os.path.join(FIXTURES, name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(directory)

    history = [{
        "kind": "meta", "schemaVersion": 1, "startedUtc": iso(T0), "tickMs": 1000,
        "leaderLockId": LEADER_LOCK, "schema": "wolverine",
        "serverVersion": "PostgreSQL 16.4 (fixture)",
    }]
    for tick in range(DURATION):
        history.append(sample(tick + 1, T0 + tick * TICK, World()))

    pods = [{"kind": "identity", "ts": iso(T0), "nodeId": n[2], "podName": n[0], "podIp": n[1]}
            for n in NODES]

    w = World()
    for agent, pod in sorted(w.running.items()):
        pods.append({"kind": "agent", "ts": iso(T0), "event": "start",
                     "agentUri": agent, "podName": pod})

    # pod-b starts agent1 while pod-a is still running it, and keeps it for 60s.
    pods.append({"kind": "agent", "ts": iso(T0 + timedelta(seconds=100)), "event": "start",
                 "agentUri": "sim://agent1", "podName": "churnsim-b"})
    pods.append({"kind": "agent", "ts": iso(T0 + timedelta(seconds=160)), "event": "stop",
                 "agentUri": "sim://agent1", "podName": "churnsim-b"})

    write(os.path.join(directory, "history.jsonl"), history)
    write(os.path.join(directory, "pods.jsonl"), sorted(pods, key=lambda r: r["ts"]))
    write(os.path.join(directory, "marks.jsonl"),
          [{"kind": "mark", "ts": iso(T0 + timedelta(seconds=90)), "label": "rollout-end"}])
    print(f"  {name}")


if __name__ == "__main__":
    main()
