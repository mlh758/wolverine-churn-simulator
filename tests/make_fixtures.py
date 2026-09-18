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

# The real values, confirmed against a live cluster: Wolverine locks on
# schemaName.GetDeterministicHashCode() ("wolverine" -> 832201495), not the unused
# LeaderLockId = 9999999 constant, and Uri.ToString() gives the leader row a trailing slash.
# Both were wrong in the first version of these fixtures, and both wrongnesses cancelled out
# into a green ledger -- the fixtures agreed with the checker instead of with Wolverine.
LEADER_LOCK = 832201495
LEADER_URI = "wolverine://leader/"

# RavenDB's leadership lock is a compare-exchange value, not an advisory lock. The key is
# "wolverine/leader/<service name, lowercased>" once RavenDbMessageStore.StartScheduledJobs has
# run -- before that the store's constructor uses the un-suffixed form, which is why the split-key
# fixture below is a plausible state and not an invented one. The five-minute expiry is
# RavenDbMessageStore.Locking's DateTimeOffset.UtcNow.AddMinutes(5).
RAVEN_LEADER_KEY = "wolverine/leader/churnsim"
RAVEN_LEADER_KEY_UNSUFFIXED = "wolverine/leader"
RAVEN_LOCK_TTL = timedelta(minutes=5)

NODES = [
    # (pod name, pod ip, node uuid, node number, backend pid)
    ("churnsim-a", "10.0.0.11", "11111111-1111-1111-1111-111111111111", 1, 101),
    ("churnsim-b", "10.0.0.12", "22222222-2222-2222-2222-222222222222", 2, 102),
    ("churnsim-c", "10.0.0.13", "33333333-3333-3333-3333-333333333333", 3, 103),
]

AGENTS = [f"sim://agent{i}/" for i in range(1, 7)]


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

        # RavenDB only. `lock_expires_in` is how far ahead of the current tick the lock's
        # ExpirationTime sits; a negative value is a lock that has expired and nobody has taken
        # over. `extra_locks` are further (key, node uuid) pairs holding leadership at the same
        # time -- which the compare-exchange primitive does nothing to prevent, because each key
        # is its own value with its own index.
        self.lock_expires_in = RAVEN_LOCK_TTL
        self.extra_locks = []

        # A tick that SUCCEEDED but is not fully trustworthy -- a query served from a stale index,
        # or one that came back short of its page limit. Not an error (there is real data on the
        # sample) and not nothing (the data may be a prefix of the cluster).
        self.warnings = []


def sample(seq, t, w, backend="postgres"):
    locks = []
    cmpxchg = []

    if backend == "ravendb":
        if w.lock_holder is not None:
            cmpxchg.append({
                "key": RAVEN_LEADER_KEY,
                "nodeId": w.lock_holder[2],
                "expiresAt": iso(t + w.lock_expires_in),
                "index": 1000 + seq,
            })
        for key, node_id in w.extra_locks:
            cmpxchg.append({
                "key": key,
                "nodeId": node_id,
                "expiresAt": iso(t + RAVEN_LOCK_TTL),
                "index": 2000 + seq,
            })
    elif w.lock_holder is not None:
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

    record = {
        "kind": "sample",
        "seq": seq,
        "ts": iso(t),
        "dbTs": iso(t + timedelta(milliseconds=3)),
        "elapsedMs": 4.2,
        "locks": locks,
        "nodes": nodes,
        "assignments": assignments,
    }

    # Absent rather than empty on PostgreSQL, because that is what the monitor writes: the field
    # is null there and the serializer drops nulls. A fixture that agrees with the checker instead
    # of with the thing being checked is the failure mode these comments exist to prevent.
    if backend == "ravendb":
        record["cmpxchg"] = cmpxchg

    if w.warnings:
        record["warnings"] = list(w.warnings)

    return record


def meta_record(backend):
    if backend == "ravendb":
        # leaderLockId is a PostgreSQL schema-name hash with no RavenDB counterpart, so the
        # monitor writes 0 rather than a plausible-looking number, and `schema` carries the
        # database name. Keep this identical to RavenClusterMonitor.DescribeAsync.
        return {
            "kind": "meta",
            "schemaVersion": 2,
            "startedUtc": iso(T0),
            "tickMs": 1000,
            "leaderLockId": 0,
            "schema": "churnsim",
            "serverVersion": "RavenDB 7.0.9 (fixture)",
            "backend": "ravendb",
            "lockKey": RAVEN_LEADER_KEY,
        }

    # No "backend" key on purpose: this is what a pre-RavenDB capture looks like, so the
    # fixtures also pin that those still load and still check as PostgreSQL.
    return {
        "kind": "meta",
        "schemaVersion": 1,
        "startedUtc": iso(T0),
        "tickMs": 1000,
        "leaderLockId": LEADER_LOCK,
        "schema": "wolverine",
        "serverVersion": "PostgreSQL 16.4 (fixture)",
    }


def build(name, mutate=None, skip=None, backend="postgres"):
    """mutate(tick, world) adjusts state in place; skip(tick) drops a sample entirely."""
    directory = os.path.join(FIXTURES, name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(directory)

    history = [meta_record(backend)]
    for tick in range(DURATION):
        if skip and skip(tick):
            continue
        t = T0 + tick * TICK
        w = World()
        if mutate:
            mutate(tick, w)
        history.append(sample(tick + 1, t, w, backend))

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
        w.running = {k: v for k, v in w.running.items() if k != "sim://agent3/"}


def no_converge(tick, w):
    """Two agents are never placed after the rollout — the assignment plane gave up."""
    if tick >= 60:
        w.placement = {k: v for k, v in w.placement.items()
                       if k not in ("sim://agent5/", "sim://agent6/")}
        w.running = {k: v for k, v in w.running.items()
                     if k not in ("sim://agent5/", "sim://agent6/")}


def split_leader(tick, w):
    """The leader row names node A while the advisory lock is held by node B."""
    if tick >= 120:
        w.lock_holder = NODES[1]


def raven_expired_lock(tick, w):
    """The RavenDB failover stall, and the fault that has no PostgreSQL counterpart.

    A compare-exchange lock is not released by the death of a session -- there is no session. It
    sits in the Raft cluster until its ExpirationTime passes AND some peer runs
    tryTakeOverIfExpiredAsync and CAS-replaces it. So the fault here is not two leaders; it is
    *no* leader, for as long as nobody troubles to look. From inside the cluster it is invisible:
    the incumbent (if it is alive at all) still believes it holds the lock, because
    HasLeadershipLock() reads a local field, and every peer sees a lock it does not own.

    Node A is deliberately left alive and registered and still owning the leader row, so that
    exactly one thing is wrong: the lock stopped being renewed and has aged past its expiry.
    """
    if tick >= 120:
        w.lock_expires_in = -timedelta(seconds=30)


def raven_truncated(tick, w):
    """The read came back short, and every check above it silently narrowed.

    RavenDB returns the assignment set through a paged query, not a SELECT. A cluster with more
    agents than the monitor's page limit gets a PREFIX of the assignment set on every sample --
    and a prefix is internally consistent. Every agent in it has exactly one live owner, the
    leader row is there, S5/S6/S7 and L1 all pass, and the run reads as a smaller healthy cluster
    rather than as a partly-unobserved one.

    Nothing about the sample says so except the warning the monitor attaches, which is why C0
    treats it as a violation and not a note: this is the exact shape of "0 violations over an
    unknown amount of observation" that the whole coverage check exists to refuse.
    """
    if tick >= 150:
        w.warnings = ["AgentAssignments read returned 2000 of 3000 — page limit hit"]


def raven_split_key(tick, w):
    """Leadership held under both spellings of the key, by two different nodes.

    RavenDbMessageStore's constructor sets the key to "wolverine/leader" and StartScheduledJobs
    then renames it to "wolverine/leader/<service>". They are two independent compare-exchange
    values: nothing about the primitive makes claiming one exclude the other, so a node that
    settled on the un-suffixed key leads alongside a node that settled on the suffixed one. The
    suffixed holder is listed first and matches the leader assignment row, so this fixture states
    the split and nothing else.
    """
    if tick >= 120:
        w.extra_locks = [(RAVEN_LEADER_KEY_UNSUFFIXED, NODES[1][2])]


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

    # The RavenDB arm. `raven-clean` is not filler: it is the evidence that the leader-side
    # checkers read compare-exchange evidence correctly and do not fire on a healthy RavenDB
    # cluster just because the locks array is empty.
    build("raven-clean", clean, backend="ravendb")
    build("raven-expired-lock", raven_expired_lock, backend="ravendb")
    build("raven-split-key", raven_split_key, backend="ravendb")
    build("raven-truncated", raven_truncated, backend="ravendb")


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
                 "agentUri": "sim://agent1/", "podName": "churnsim-b"})
    pods.append({"kind": "agent", "ts": iso(T0 + timedelta(seconds=160)), "event": "stop",
                 "agentUri": "sim://agent1/", "podName": "churnsim-b"})

    write(os.path.join(directory, "history.jsonl"), history)
    write(os.path.join(directory, "pods.jsonl"), sorted(pods, key=lambda r: r["ts"]))
    write(os.path.join(directory, "marks.jsonl"),
          [{"kind": "mark", "ts": iso(T0 + timedelta(seconds=90)), "label": "rollout-end"}])
    print(f"  {name}")


if __name__ == "__main__":
    main()
