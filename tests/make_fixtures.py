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
MYSQL_LOCK_NAME = f"wolverine_{LEADER_LOCK}"

RAVEN_LEADER_KEY = "wolverine/leader/churnsim"
RAVEN_LEADER_KEY_UNSUFFIXED = "wolverine/leader"
RAVEN_LOCK_TTL = timedelta(minutes=5)

# The replicated store's members, as k8s/safetylab-ravendb-cluster.yaml names them. Member 0 is
# the monitor's primary view.
REPLICA_URLS = [f"http://ravendb-{i}.ravendb.default.svc.cluster.local:8080" for i in range(3)]

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

        # Nodes that have stopped writing their health_check, mapped to the instant it froze.
        # That is what an app-to-store partition looks like from the database: the node is still
        # registered, still owns its rows, and has simply stopped saying anything. E9/K2.
        self.silent = {}

        # A tick that SUCCEEDED but is not fully trustworthy -- a query served from a stale index,
        # or one that came back short of its page limit. Not an error (there is real data on the
        # sample) and not nothing (the data may be a prefix of the cluster).
        self.warnings = []

        # Replicated RavenDB only (E7). One entry per store member, keyed by member index; the
        # monitor's primary view is member 0. `raft_leader` None is what a minority member answers
        # while cut off. `divergent` is (agent uri, node uuid) rows the member holds with a
        # different owner than the primary; `missing` is agent uris it has no document for;
        # `conflicts` is the member's CountOfConflicts. `lock_holder_override` makes that member
        # report a different owner of the leadership key than the primary does -- which is what
        # a compare-exchange value looks like from a member that stopped receiving Raft commits.
        self.replicas = None

    def replicate(self):
        """Turn this world into a healthy three-member view of itself."""
        self.replicas = {
            i: {"raft_leader": "A", "raft_state": "Leader" if i == 0 else "Follower",
                "conflicts": 0, "divergent": [], "missing": [], "lock_holder_override": None,
                "error": None}
            for i in range(3)
        }


def sample(seq, t, w, backend="postgres"):
    locks = []
    cmpxchg = []

    if backend == "ravendb":
        def lock_rows(node=None, holder=None):
            rows = []
            holder = holder if holder is not None else w.lock_holder
            if holder is not None:
                rows.append({
                    "key": RAVEN_LEADER_KEY,
                    "nodeId": holder[2],
                    "expiresAt": iso(t + w.lock_expires_in),
                    "index": 1000 + seq,
                })
            for key, node_id in w.extra_locks:
                rows.append({
                    "key": key,
                    "nodeId": node_id,
                    "expiresAt": iso(t + RAVEN_LOCK_TTL),
                    "index": 2000 + seq,
                })
            if node is not None:
                for row in rows:
                    row["node"] = node
            return rows

        if w.replicas is None:
            cmpxchg = lock_rows()
        else:
            # Every member's rows, tagged with their source, exactly as RavenClusterMonitor
            # writes them. Members that agree collapse into one holder in RunHistory.
            for i, r in sorted(w.replicas.items()):
                if r["error"] is not None:
                    continue
                cmpxchg.extend(lock_rows(REPLICA_URLS[i], r["lock_holder_override"]))
    elif backend == "mysql":
        if w.lock_holder is not None:
            # A MySQL named lock, exactly as MySqlClusterMonitor writes one: the "pid" is a
            # connection id, objId is the integer parsed out of the lock's NAME, applicationName
            # carries that name because MySQL has no application_name, and classId/objSubId are
            # PostgreSQL's two-part advisory key and stay 0. Different columns, same evidence --
            # which is the claim the mysql-* fixtures exist to test.
            locks.append({
                "pid": w.lock_holder[4],
                "classId": 0,
                "objId": LEADER_LOCK,
                "objSubId": 0,
                "granted": True,
                "applicationName": MYSQL_LOCK_NAME,
                "clientAddr": w.lock_holder[1],
                "backendState": "Sleep",
                "backendStart": iso(T0 - timedelta(seconds=30)),
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
        {"id": n[2], "nodeNumber": n[3], "healthCheck": iso(w.silent.get(n[2], t)), "description": n[0]}
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

    if w.replicas is not None:
        record["replicas"] = []
        for i, r in sorted(w.replicas.items()):
            if r["error"] is not None:
                record["replicas"].append({
                    "url": REPLICA_URLS[i], "nodeCount": 0, "assignmentCount": 0,
                    "divergent": [], "missing": [], "error": r["error"],
                })
                continue
            record["replicas"].append({
                "url": REPLICA_URLS[i],
                "nodeTag": "ABC"[i],
                "raftLeader": r["raft_leader"],
                "raftState": r["raft_state"],
                "conflicts": r["conflicts"],
                "nodeCount": len(nodes),
                "assignmentCount": len(assignments) + len(r["divergent"]) - len(r["missing"]),
                "divergent": [{"id": a, "nodeId": n, "started": iso(T0)} for a, n in r["divergent"]],
                "missing": list(r["missing"]),
            })

    return record


def meta_record(backend, replicated=False):
    if backend == "ravendb":
        # leaderLockId is a PostgreSQL schema-name hash with no RavenDB counterpart, so the
        # monitor writes 0 rather than a plausible-looking number, and `schema` carries the
        # database name. Keep this identical to RavenClusterMonitor.DescribeAsync.
        record = {
            "kind": "meta",
            "schemaVersion": 3,
            "startedUtc": iso(T0),
            "tickMs": 1000,
            "leaderLockId": 0,
            "schema": "churnsim",
            "serverVersion": "RavenDB 7.0.9 (fixture)",
            "backend": "ravendb",
            "lockKey": RAVEN_LEADER_KEY,
        }
        if replicated:
            record["replicas"] = list(REPLICA_URLS)
        return record

    if backend == "mysql":
        # Same leaderLockId as PostgreSQL, and that is the point rather than a copy-paste:
        # MySqlNodePersistence derives it from the SAME schemaName.GetDeterministicHashCode(),
        # and only the spelling of the lock differs. Keep this identical to
        # MySqlClusterMonitor.DescribeAsync.
        return {
            "kind": "meta",
            "schemaVersion": 3,
            "startedUtc": iso(T0),
            "tickMs": 1000,
            "leaderLockId": LEADER_LOCK,
            "schema": "wolverine",
            "serverVersion": "8.0.39 / mdl instrument YES (fixture)",
            "backend": "mysql",
            "lockKey": MYSQL_LOCK_NAME,
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


def build(name, mutate=None, skip=None, backend="postgres", replicated=False, marks=None):
    """mutate(tick, world) adjusts state in place; skip(tick) drops a sample entirely.

    replicated=True gives every tick a healthy three-member view before mutate runs, so a
    replicated fixture states only its fault. marks overrides the default rollout-end marker.
    """
    directory = os.path.join(FIXTURES, name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(directory)

    def world(tick):
        w = World()
        if replicated:
            w.replicate()
        if mutate:
            mutate(tick, w)
        return w

    history = [meta_record(backend, replicated)]
    for tick in range(DURATION):
        if skip and skip(tick):
            continue
        t = T0 + tick * TICK
        history.append(sample(tick + 1, t, world(tick), backend))

    # Pod-side stream. Residencies are derived from the same World the samples use, so a
    # fixture only has to state its fault once.
    pods = []
    for pod, ip, node_id, _, _ in NODES:
        pods.append({"kind": "identity", "ts": iso(T0), "nodeId": node_id,
                     "podName": pod, "podIp": ip})

    previous = {}
    for tick in range(DURATION):
        t = T0 + tick * TICK
        w = world(tick)
        current = {(agent, pod) for agent, pod in w.running.items()}

        for pair in current - set(previous):
            pods.append({"kind": "agent", "ts": iso(t), "event": "start",
                         "agentUri": pair[0], "podName": pair[1]})
        for pair in set(previous) - current:
            pods.append({"kind": "agent", "ts": iso(t), "event": "stop",
                         "agentUri": pair[0], "podName": pair[1]})
        previous = current

    if marks is None:
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


def shed_nowhere(tick, w):
    """GH-4590: the collapse to one node, and the survivor detaches an agent with nowhere to put it.

    At tick 60 nodes B and C leave. Their four agents stop running and go unplaced, and that is
    CORRECT -- one node is left, it has no headroom, and leaving agents unassigned rather than
    overloading the survivor is capacity-aware assignment doing precisely its job. None of those
    four may be a violation.

    At tick 120 the survivor also gives up agent1, which it was running, and nothing re-places it.
    That one is the bug: the shed pass ran before the "nobody has headroom" early return, so the
    agent was detached with no destination and stopped for good.

    The whole point of the fixture is that the two are indistinguishable by COUNT -- five of six
    agents are unplaced at the end either way, and L1 says exactly that and cannot tell you which
    five. S12 keys on the transition and on whether the node stayed in the cluster, so it names
    agent1 and stays silent about the other four.
    """
    if tick >= 60:
        for node in (NODES[1], NODES[2]):
            w.live.discard(node[2])
        w.placement = {k: v for k, v in w.placement.items() if v == NODES[0][2]}
        w.running = {k: v for k, v in w.running.items() if v == NODES[0][0]}
    if tick >= 120:
        w.placement = {k: v for k, v in w.placement.items() if k != "sim://agent1/"}
        w.running = {k: v for k, v in w.running.items() if k != "sim://agent1/"}


def split_leader(tick, w):
    """The leader row names node A while the advisory lock is held by node B."""
    if tick >= 120:
        w.lock_holder = NODES[1]


# ------------------------------------------------------- the lock-session kill (E8)

# The leader's backend is terminated at the mark. A session-level advisory lock has no expiry and
# no owner column, so its release is instantaneous and nothing is written anywhere saying so --
# the only trace is the lock's absence from pg_locks. The node keeps believing it leads, because
# lock-loss detection is a `select 1` liveness ping that reports the last known state when its
# gate is busy.
LOCK_KILL_MARKS = [
    {"kind": "mark", "ts": iso(T0 + timedelta(seconds=60)), "label": "lock-kill"},
]

# The control arm marks itself differently, because it asserts the OPPOSITE outcome:
# pg_cancel_backend interrupts the statement and the server keeps the session, and so the lock.
# One shared label made K1 fail every correct control run — found by running it, 2026-09-19.
LOCK_CANCEL_MARKS = [
    {"kind": "mark", "ts": iso(T0 + timedelta(seconds=60)), "label": "lock-cancel"},
]

# The same node, reconnected. PostgreSQL gives the new connection a different backend pid, which
# is exactly why K1 reads the pid: a leader that re-attains the lock seconds later still lost it,
# and a check comparing node identity instead would call that an undisturbed cluster.
NODE_A_RECONNECTED = (NODES[0][0], NODES[0][1], NODES[0][2], NODES[0][3], 201)


def lock_kill(tick, w):
    """E8: the leader's lock session is terminated, and for 20s nothing holds the lock.

    Node A stays alive, registered, and still owns the wolverine://leader/ row the whole time --
    that is the fault, not an omission. The server took the lock away from a node that was never
    told, so for 20 seconds the cluster has a leader row backed by no server-side claim, which is
    the GH-2602 shape and what S3 reports. At tick 80 node A notices and re-attains the lock on a
    new connection: the same node, a new pid.
    """
    if 60 <= tick < 80:
        w.lock_holder = None
    elif tick >= 80:
        w.lock_holder = NODE_A_RECONNECTED


def lock_kill_noop(tick, w):
    """The marks say a lock session was killed; the lock never left pid 101.

    pg_terminate_backend answers false rather than throwing when the pid has gone, the resolved
    pid can belong to some other connection, and a monitor watching the wrong lock id sees a
    plausible unchanging lock forever. All three produce this: a run filed as a lock-loss
    measurement, taken against a cluster that never lost its lock, with every other check passing
    honestly. K1 exists to refuse exactly this.
    """


def lock_cancel_moved(tick, w):
    """The control arm, and the lock moved anyway.

    The server keeps a cancelled session and its advisory lock (checked against PostgreSQL 17.7),
    so a lock that leaves after a cancel says the CLIENT dropped its connection in response to the
    cancellation — a finding about the node in its own right, and a run that is not the control
    arm it was filed as. The same sample shape as `lock-kill`; only the mark differs, which is the
    whole point.
    """
    if 60 <= tick < 80:
        w.lock_holder = None
    elif tick >= 80:
        w.lock_holder = NODE_A_RECONNECTED


# ------------------------------------------------- the app-to-store partition (E9)

DB_CUT_MARKS = [
    {"kind": "mark", "ts": iso(T0 + timedelta(seconds=60)), "label": "db-cut-start"},
    {"kind": "mark", "ts": iso(T0 + timedelta(seconds=180)), "label": "db-cut-heal"},
]

CUT_AT = T0 + timedelta(seconds=60)


def db_cut(tick, w):
    """E9: the leader is cut off from PostgreSQL, and keeps the lock anyway.

    The inverse of E8. There the session died and the lock died with it; here the session is
    perfectly alive on the server -- it is the CLIENT that cannot reach it -- so the advisory lock
    stays held by a node that has stopped writing. Its health_check freezes at the moment of the
    cut while both peers keep heartbeating, which is the only thing the store shows and is what K2
    reads.

    Nothing else moves: node A stays registered (the node that would eject a stale registration is
    the leader, and the leader is the one cut off), the leader row still names it, and all six
    agents stay placed. So the assignment table reads as a perfectly healthy fully-placed cluster
    for the whole outage -- the same "nothing in the store shows it" shape as the RavenDB stall.
    S11 is the check that refuses to let that read as health.
    """
    if 60 <= tick < 180:
        w.silent = {NODES[0][2]: CUT_AT}


def db_cut_noop(tick, w):
    """The marks say a cut ran; every node kept heartbeating throughout.

    A rule that matched nothing, a pod that came back on a new IP outside every rule, a cut aimed
    at the wrong pod. The cluster was intact for the whole window and every other check in the run
    passes honestly over it. K2 exists to refuse exactly this, and S11 must stay quiet -- a lock
    held by a node that never stopped heartbeating is not a stall.
    """


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


# ------------------------------------------------------- the replicated store (E7)

PARTITION_MARKS = [
    {"kind": "mark", "ts": iso(T0 + timedelta(seconds=60)), "label": "partition-start"},
    {"kind": "mark", "ts": iso(T0 + timedelta(seconds=180)), "label": "partition-heal"},
]


def cut_off(w, member):
    """What a minority member looks like from outside: it still answers, it knows the members,
    it has no Raft leader and has fallen to Candidate. Nothing about its data has changed yet."""
    w.replicas[member]["raft_leader"] = None
    w.replicas[member]["raft_state"] = "Candidate"


def raven_partition(tick, w):
    """The split, as E7 predicts it, on member 2 between the two marks.

    Leadership: the majority (members 0 and 1) took the lock over -- node B now owns
    wolverine/leader/churnsim there -- while the cut-off member 2 still serves the value it last
    committed, naming node A. Two members, two owners of one key: S1 across members.

    Assignments: node A on the minority side re-claimed agent2 and agent3 on member 2 (plain
    session writes succeed there), while the majority still places them on nodes B and C. So
    member 2 disagrees with the primary on two rows: S9.

    The leader assignment row on the primary follows the majority (node B), so S2/S3 see one
    coherent leader on the primary view and stay quiet -- this fixture is the split, and only
    the split. Recovery after the heal is left out on purpose so the interval is unambiguous.
    """
    if 60 <= tick < 180:
        cut_off(w, 2)
        w.lock_holder = NODES[1]
        w.leader = NODES[1][2]
        w.replicas[2]["lock_holder_override"] = NODES[0]
        w.replicas[2]["divergent"] = [("sim://agent2/", NODES[0][2]), ("sim://agent3/", NODES[0][2])]
    elif tick >= 180:
        w.lock_holder = NODES[1]
        w.leader = NODES[1][2]


def raven_conflicts(tick, w):
    """A partition that took (member 2 leaderless between the marks, so P1 is satisfied) and,
    after the heal, member 1 reporting two documents in conflict: the store's own statement that
    two members accepted incompatible writes to one AgentAssignments document. Every member
    agrees on ownership throughout, so only S10 has anything to say."""
    if 60 <= tick < 180:
        cut_off(w, 2)
    elif tick >= 180:
        w.replicas[1]["conflicts"] = 2


def raven_partition_noop(tick, w):
    """The marks say a partition ran; no member ever lost its Raft leader. The cut did not take
    -- a firewall rule that matched nothing, a client that rerouted -- and every other check in
    this run passed over an intact cluster. P1 exists to refuse exactly this."""


def main():
    os.makedirs(FIXTURES, exist_ok=True)
    print("fixtures:")
    build("clean", clean)
    build("orphan-lock", orphan_lock)
    build("stranded", stranded)
    build("no-converge", no_converge)
    build("split-leader", split_leader)
    build("shed-nowhere", shed_nowhere)
    build("gap", clean, skip=lambda tick: 150 <= tick < 180)

    # E8. `lock-kill` is the fault landing (S3: a leader row with no lock behind it);
    # `lock-kill-noop` is the nemesis that did not fire, which every other check passes over.
    build("lock-kill", lock_kill, marks=LOCK_KILL_MARKS)
    build("lock-kill-noop", lock_kill_noop, marks=LOCK_KILL_MARKS)

    # The control arm, both ways round. `lock-cancel` is the SAME unchanged cluster as
    # `lock-kill-noop` under the other mark, and it must come back clean — those two fixtures
    # together are what stop K1 reading one arm's success as the other's failure.
    build("lock-cancel", lock_kill_noop, marks=LOCK_CANCEL_MARKS)
    build("lock-cancel-moved", lock_cancel_moved, marks=LOCK_CANCEL_MARKS)

    # E9, the app-to-store cut. `db-cut` is the fault landing: S11 refuses a lock held by a node
    # that stopped heartbeating, and K2 confirms the cut took. `db-cut-noop` is the cut that did
    # not take, where K2 is the only thing that can tell the two runs apart.
    build("db-cut", db_cut, marks=DB_CUT_MARKS)
    build("db-cut-noop", db_cut_noop, marks=DB_CUT_MARKS)
    build_dup("dup-agent", pods_from=T0, overlap=(100, 160))

    # The same duplicate, but it happened and healed BEFORE the first sample. The pod logs are
    # harvested whole from the node tailer, so a pod alive at capture start carries its history
    # -- including an earlier rollout's duplicates, which are real but are not this run's
    # finding. S5 must stay green and say in a note that it saw them. Without this fixture a
    # capture started twenty minutes after a rollout read as a failed run.
    build_dup("dup-agent-before", pods_from=T0 - timedelta(seconds=600), overlap=(-300, -240))
    build_restart_shortfall()

    # The MySQL arm. Same two claims as raven-clean, from the other direction: a healthy MySQL
    # capture must come back wholly clean (so the leader checks are not firing on the changed
    # lock-row shape), and the SAME fault must produce the SAME check ids as on PostgreSQL (so
    # they are not passing vacuously over evidence they no longer recognise). If those two ever
    # diverge, the arms are not comparable and RESULTS.md cannot put their numbers side by side.
    build("mysql-clean", clean, backend="mysql")
    build("mysql-orphan-lock", orphan_lock, backend="mysql")

    # The RavenDB arm. `raven-clean` is not filler: it is the evidence that the leader-side
    # checkers read compare-exchange evidence correctly and do not fire on a healthy RavenDB
    # cluster just because the locks array is empty.
    build("raven-clean", clean, backend="ravendb")
    build("raven-expired-lock", raven_expired_lock, backend="ravendb")
    build("raven-split-key", raven_split_key, backend="ravendb")
    build("raven-truncated", raven_truncated, backend="ravendb")

    # The replicated store (E7). `raven-replicas-clean` pins that three agreeing members read as
    # ONE leader and no divergence -- without it, a healthy cluster would trip S1 three times over
    # and S9 would never have been seen quiet.
    build("raven-replicas-clean", clean, backend="ravendb", replicated=True)
    build("raven-partition", raven_partition, backend="ravendb", replicated=True, marks=PARTITION_MARKS)
    build("raven-conflicts", raven_conflicts, backend="ravendb", replicated=True, marks=PARTITION_MARKS)
    build("raven-partition-noop", raven_partition_noop, backend="ravendb", replicated=True, marks=PARTITION_MARKS)


def build_dup(name, pods_from, overlap):
    """Built separately: it is the one fixture whose fault lives in the pod stream only.

    The assignment table keeps saying agent1 belongs to node A the whole time. Only the
    AGENT-START/STOP logs show pod-b running it too — which is exactly why S5 reads the pod
    stream rather than trusting the table.

    pods_from is when the pods announced themselves and started their agents; overlap is
    (start, stop) of pod-b's stint on agent1, in seconds relative to T0, the first sample.
    """
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

    pods = [{"kind": "identity", "ts": iso(pods_from), "nodeId": n[2], "podName": n[0], "podIp": n[1]}
            for n in NODES]

    w = World()
    for agent, pod in sorted(w.running.items()):
        pods.append({"kind": "agent", "ts": iso(pods_from), "event": "start",
                     "agentUri": agent, "podName": pod})

    # pod-b starts agent1 while pod-a is still running it, and keeps it for 60s.
    pods.append({"kind": "agent", "ts": iso(T0 + timedelta(seconds=overlap[0])), "event": "start",
                 "agentUri": "sim://agent1/", "podName": "churnsim-b"})
    pods.append({"kind": "agent", "ts": iso(T0 + timedelta(seconds=overlap[1])), "event": "stop",
                 "agentUri": "sim://agent1/", "podName": "churnsim-b"})

    write(os.path.join(directory, "history.jsonl"), history)
    write(os.path.join(directory, "pods.jsonl"), sorted(pods, key=lambda r: r["ts"]))
    write(os.path.join(directory, "marks.jsonl"),
          [{"kind": "mark", "ts": iso(T0 + timedelta(seconds=90)), "label": "rollout-end"}])
    print(f"  {name}")


NODE_B_RESTARTED = "bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb"
RESTART_TICK = 100


def build_restart_shortfall():
    """A follower dies ungracefully and comes back in the same pod under a new node id; one of
    its agents is never placed again.

    That is the ungraceful-death shortfall (RESULTS.md 2026-09-19), and S12 must stay quiet about
    it: the agent was stopped by its node dying, not shed by a node that stayed. Before this
    fixture S12 judged by the pod's LATEST identity -- the survivor, which is in the cluster --
    and so reported every victim's agent on any capture spanning a kill. L1 is what reports the
    unplaced agent.
    """
    name = "restart-shortfall"
    directory = os.path.join(FIXTURES, name)
    shutil.rmtree(directory, ignore_errors=True)
    os.makedirs(directory)

    pod_b, ip_b, node_b, _, _ = NODES[1]
    t_restart = T0 + RESTART_TICK * TICK

    history = [meta_record("postgres")]
    for tick in range(DURATION):
        w = World()
        if tick >= RESTART_TICK:
            # agent2 and agent5 were on B. agent5 comes back on B's new process; agent2 never does.
            del w.placement["sim://agent2/"]
            del w.running["sim://agent2/"]
        record = sample(tick + 1, T0 + tick * TICK, w)
        if tick >= RESTART_TICK:
            for n in record["nodes"]:
                if n["id"] == node_b:
                    n["id"] = NODE_B_RESTARTED
            for a in record["assignments"]:
                if a["nodeId"] == node_b:
                    a["nodeId"] = NODE_B_RESTARTED
        history.append(record)

    pods = [{"kind": "identity", "ts": iso(T0), "nodeId": n[2], "podName": n[0], "podIp": n[1]}
            for n in NODES]
    for agent, pod in sorted(World().running.items()):
        pods.append({"kind": "agent", "ts": iso(T0), "event": "start", "agentUri": agent, "podName": pod})

    # The new process announces itself; the dead one logged no AGENT-STOP for anything.
    pods.append({"kind": "identity", "ts": iso(t_restart), "nodeId": NODE_B_RESTARTED,
                 "podName": pod_b, "podIp": ip_b})
    pods.append({"kind": "agent", "ts": iso(t_restart + TICK), "event": "start",
                 "agentUri": "sim://agent5/", "podName": pod_b})

    write(os.path.join(directory, "history.jsonl"), history)
    write(os.path.join(directory, "pods.jsonl"), sorted(pods, key=lambda r: r["ts"]))
    write(os.path.join(directory, "marks.jsonl"),
          [{"kind": "mark", "ts": iso(T0 + timedelta(seconds=90)), "label": "rollout-end"}])
    print(f"  {name}")


if __name__ == "__main__":
    main()
