# Results

Measured runs, newest first. Each entry is dated and states the build, the configuration and
the caveats it was taken under, so results accumulate here instead of being trimmed out of the
README every time a new one lands.

**Read the caveats.** A number without its configuration is not a result — `AgentStartBatchSize`
alone moved a convergence time by minutes, and a knob left set from a previous experiment is the
easiest way to publish a comparison that is really measuring something else.

**Target framework: net10.0, as of 2026-09-11.** ChurnSim targets net10.0 and every number from
that date forward is taken on it. net8 and net9 are both near end of support and net11 is already
out, so pinning the rig to one current LTS-track runtime is what keeps runs comparable to each
other rather than to a retired baseline. Entries dated before 2026-09-11 were taken on net9.0
unless they say otherwise; where a net10.0 rerun exists the older numbers were dropped rather than
kept alongside it — git history has them.

## 2026-09-22 — the shortfall is a 21-minute outage, and releasing a departed node's in-flight commands closes it

Two arms, interleaved on one cluster, `reset-schema` between, each gated on an actual `safetylab settle`
(500 placed) before the nemesis rather than on a fixed delay. PostgreSQL, net10.0, 3 replicas, 500 agents,
`SIM_START_DELAY_MS=500`, stock knobs, `just leader-kill`. `NodeStopped` 0 → 0 in both, so both exercised
the ungraceful path.

`6.40.0-rosterrelease.2` is a **local, unreleased** build of `main` carrying one change: the leader
abandons the command lane of every node absent from the membership it reads each health check, releases
the agent claims those commands held, and drops the matching pending-assignment ledger entries.

| t | stock 6.39.1 | `6.40.0-rosterrelease.2` | |
|---|---|---|---|
| 1 s | 500 | 500 | pre-kill |
| ~6–7 s | 458 | 446 | SIGKILL |
| 12–18 s | 500 | 478 → 500 | the corpse's rows are still counted |
| ~67 s | **459** | **479** | ejection |
| **73 s** | 459 | **500** | the release lands |
| **1292 s** | **500** | 500 | the reply window lapses |
| polls at 500 | 104 / 328 | **161 / 164** | |

Both end at `running=500 assigned=500 duplicated=0 orphaned=0 missing=0`. Stock is short by exactly 41 for
**20 minutes** with every check green on both sides of the comparison; the fix is short for one
health-check period.

### The mechanism, measured three ways

**Timing.** `AgentBatchTimeouts.ReplyWindowFor(41)` = `30 + 41 x 30` = 1260 s, from a dispatch at ~t+28 s,
predicts stock recovery at t≈1288 s. Observed: **1292 s**.

**The leader's log**, stock, at that moment:

```
warn: Node ef0ce880… confirmed stopping 0 of 41 agents being reassigned to node d126a179…;
      41 remain unconfirmed and will be re-evaluated: sim://agent355/, sim://agent356/, …
```

**The same batch on the fix build**, resolved at the roster check instead:

```
info: Released 41 agent(s) held by commands aimed at nodes that have left the cluster;
      they will be re-placed on the next assignment evaluation
```

41 is `held - ceil(500/4)`, with no fitting, and it is the same 41 on every released build from 6.35.0
forward (2026-09-19 entry).

### Why the release is driven by the roster and not by the ejection

The obvious wiring — release from inside `ejectStaleNodes`, against the node just deleted — **does not
work, and measures as no fix at all**: 1352 s against the stock 1292 s, twice running
(`runs/leader-kill-postgres-ejectrelease2{,-run2}/`). Ejecting a stale row is not the leader's privilege;
`ejectStaleNodes` spares only the *current leader's* row, so once leadership has moved off the corpse any
node may delete it, and on a three-node cluster a follower won that race in all three runs observed
(`DormantNodeEjected … Health check on Node 4` / `Node 3` / `Node 1`, leader Node 2 each time). The wedged
commands exist only on the **leader's** dispatcher, so the release fired on a node holding nothing, logged
nothing, and left the leader stranded for the full reply window. Asking "is this node still in the
membership I just read" gives the same answer whoever performs the delete — and in the final run a
non-leader ejected again, and the leader released anyway.

That failed arm is the reason this entry exists in this shape: an end-state snapshot at 420 s would have
called both builds identical, and a 73 s recovery is only distinguishable from a 1292 s one if the watch
outlives the reply window.

### What this does not establish

- **Not a released package.** A local build of an unmerged branch.
- **One run per arm.** The counts are deterministic and stock's timing matched a prediction to within a
  poll interval, so this is not a noisy measurement — but it is still n=1 per arm, and the ejection race
  above is a reminder that one run can hide a race entirely.
- **PostgreSQL only.** The 2026-09-19 entry shows the same shortfall on RavenDB via `follower-kill`; the
  fix has not been re-measured there.
- **The long-held warning is now measured**, unlike the earlier attempt: both failed-arm runs logged
  `41 agent assignment(s) have been held pending for longer than 00:05:00 …` naming the oldest agent and
  its destination. Nothing else in the cluster reports this state.

Run directories: `runs/leader-kill-postgres-{6391-verify,rosterrelease2}/`, each with `timeline.tsv` and
the captured pod logs.

## 2026-09-19 — a surviving leader under-assigns by ~41/500 after an ungraceful node death, on both backends

net10.0, 3 replicas, 500 agents, stock knobs unless stated. `just reset-schema` between every
build; each arm gated on an actual `running=500 assigned=500` snapshot before the nemesis fired,
not on a fixed delay. Reproduces on every released version tested, 6.35.0 through 6.39.1.

**A leader that survives a node's ungraceful death does not re-place all of its agents. ~41 of 500
assignment rows go unwritten, the cluster sits there for the length of this watch, and
`assigned == running` throughout — so no checker, and nothing inside Wolverine, reports a problem.**

> **Correction, 2026-09-22 — "permanently" was wrong.** Every arm below was watched for 420 s. The
> agents are held by a batched reassignment whose source is the dead node, and that batch's reply
> window is `30 + 41 x 30` = **1260 s**, so no watch here could outlive it. Re-run at 1800 s, stock
> PostgreSQL recovers on its own at **t=1292 s**. The finding is a ~21-minute silent under-placement,
> not a permanent one; everything else below — the count, the determinism, the 2x2, the mechanism —
> stands as measured. See the 2026-09-22 entry.

### The 2×2 that isolates it

| nemesis | backend | leadership changes? | leader rebalances? | settles at |
|---|---|---|---|---|
| leader-kill | PostgreSQL | yes, a peer takes over in ~6 s | yes — victim rejoins as a new node at ~t+60 s | **459** |
| leader-kill | RavenDB | no for 303 s, then the victim's own restart reclaims it | **no** | 500 ✅ |
| **follower-kill** | **RavenDB** | **no — the leader is untouched for the whole watch** | **yes** | **458** |

The RavenDB leader-kill row is the 2026-09-18 entry below (6.39.0); the follower-kill row is 6.39.1.

It is **not** PostgreSQL-specific and it does **not** involve leadership transfer. The RavenDB
leader-kill looks clean only because nothing ever rebalances in it: the compare-exchange lease has
to expire before anyone leads again, and by then the victim is back. The single factor common to
both failing arms is a surviving leader redistributing after an ungraceful death — which is why
`scripts/follower-kill.sh` exists, and why it is the minimal reproducer rather than leader-kill.

### Version matrix — PostgreSQL leader-kill

| build | start delay | pre-kill | settles | unassigned | `wolverine://leader` row |
|---|---|---|---|---|---|
| 6.39.1 | 500 ms | 500 | 459 | 41 | present |
| 6.39.0 | 500 ms | 500 | 459 | 41 | present |
| 6.36.0 (the GH-3987 sweep lands) | 500 ms | 500 | 459 | 41 | present |
| 6.35.0 (pre-sweep) | 500 ms | 500 | 459 | 41 | **absent** |
| 6.39.1 | **0 ms** | 500 | 459 | 41 | **absent** |

Deterministic: exactly 41, across four versions and both start-delay settings. Not a race.
Unaffected by #4404 / #4407 — present in 6.35.0, before the sweep existed.

### What it is not

- **Not GH-3987.** That is *divergence* — the row says node A owns the agent and A is not running
  it. The node-side sweep heals it by comparing local runners against local rows. This is
  *absence*: `running=459 assigned=459 duplicated=0 orphaned=0 missing=0`, agreed on both sides,
  and 41 URIs with no row on any node. The sweep is working correctly and has nothing to act on —
  nothing in the node-side path is responsible for noticing the assignment *set* is short.
- **Not capability matching or an embargo.** All three surviving nodes advertise 501 capabilities
  (500 sim + durability) at the end of every run, and the restrictions table is empty.
- **Not a phantom node in the grid.** Exactly three node rows survive, all heartbeating 0–2 s.
- **Not slow starts.** `SIM_START_DELAY_MS=0`, verified from each live pod's own `printenv` after
  both the override rollout and the `reset-schema` bounce, still lands on 459.
- **Not the graceful path.** E2 (12 rolling deploys, same cluster and build) is 12/12 clean at
  500/500 with 10 `LeadershipAssumed` events — the handover window was genuinely exercised. Graceful
  shutdown releases agents cleanly; SIGKILL does not.

The unassigned URIs are scattered rather than contiguous (e.g. 104, 137, 216, 223, 224, 226, 227,
229, 230, 239, 240, 241 …), which also argues against a truncated batch.

### Scope

The trigger is narrow, which is likely why it is not already known: the node has to die
*ungracefully* (SIGKILL, or anything that skips Wolverine's shutdown) while a leader survives to
redistribute its agents. A rolling deploy does not do this — shutdown runs, agents are released
cleanly, and 12/12 rollouts settle at 500. Nor does losing a lock without losing the process, or
losing the database connection. A pod OOM-killed or evicted mid-operation does.

### Reproducing

```bash
just deploy 6.39.1            # or any build from 6.35.0 forward
just reset-schema
# wait for a real 500/500 -- `safetylab snapshot`, not a fixed sleep
just follower-kill 420
```

Run directories: `runs/leader-kill-postgres-{STOCK,6391,636,635,nodelay2}/` and
`runs/follower-kill-raven-6391/`. Each carries `db-state.txt`
(the unassigned URIs, per-node counts, capability counts, node records) and `pods.log`.

## 2026-09-19 — E9 app↔DB partition: a PostgreSQL leader cut off from its database keeps the lock, keeps the leader row, and never notices

The complement to E8, same cluster and build, run an hour later. Wolverine **6.39.0**, net10.0,
`postgres:16-alpine`, 3 replicas, 500 agents, stock knobs. The leader's pod was cut from the `pg`
pod — both directions, `DROP` rules in the minikube node's `FORWARD` chain keyed on pod IPs — for
**180 s**, then healed and watched for 120 s. Nothing was terminated and nothing was killed. Run
directory `runs/db-partition-20260919T150550Z`.

**E8 asked what happens when the lock connection dies visibly. This asks what happens when it dies
silently, and the answers are opposite.**

| | |
|---|---|
| the cut | 180 s, `churnsim-…-kz6rt` (10.244.0.14) ↮ `pg-…-tjw6x` (10.244.0.2). Victim container restarts: 0 → 0, so the pod never came back on a new IP outside the rules |
| did it take? | **yes** (K2): the node stopped heartbeating for **179 s of the 181 s window** while both peers kept going |
| the lock | **never moved.** Held by backend pid 841 — the isolated node's session — for the entire outage. The session is alive on the *server*; it is the client that cannot reach it, so there is nothing to release it and no peer can take it |
| the leader row | never changed. `wolverine://leader/` named the unreachable node throughout |
| ejection | **never happened.** The node stayed registered for ~3× its silence budget. The actor that ejects a stale registration is the leader, and the leader is the node that is cut off — so the corpse blocks its own cleanup, exactly as on the RavenDB arm, by a different route |
| what the node concluded | **nothing.** 0 step-downs, 0 `Lost advisory-lock connection`. It logged 10 × `Error writing the heartbeat for node 2`, `Error trying to perform agent health checks`, and a stream of `DatabaseControlListener` / `DurabilityAgent` failures — it knows its writes are failing and does not conclude it has lost leadership |
| what the store showed | **nothing.** `placed` read 500/500 for the whole outage; S1, S2, S3 and S4 all pass |
| duplicates | none (S5); `safetylab overlaps`: 0 |
| recovery | immediate — L1 converged **0.3 s** after the heal mark and held 128 s |
| checkers | **S11 FAILS** (167 s), K2 passes, everything else green |

**The contrast with E8, in one line each from the same cluster:**

| fault | the victim's own log | the lock |
|---|---|---|
| `pg_terminate_backend` (E8) | `Lost advisory-lock connection … clearing held lock ids 832201495` → `stepping down … Triggering a new leadership election` | released; a peer had it inside 1 s |
| app↔DB partition (E9) | 0 step-downs; 10 × `Error writing the heartbeat` | held by the unreachable node for the full 180 s |

So the detection path 6.39.0 has is **connection-break-shaped**. It fires on a FATAL from the
server and not on "my writes keep failing" — and those are the same event from the operator's
point of view. A pooler restart, a `pg_terminate_backend`, an idle-session timeout all hit the
first path and are handled well. A network partition, a hung database, a saturated link, an
iptables change all hit the second and are not detected at all.

**S11 is the only check that catches it, and that is the point.** S4 cannot: the node is never
ejected, so it never becomes "departed". S1/S2/S3 cannot: the lock and the row agree perfectly —
they just both name a node that is not there. L1 cannot: one holder, 500/500 placed, no dangling
assignment — a stalled cluster satisfies every clause of convergence. Before S11 this run produced
a fully green report over a cluster whose leader had been unreachable for three minutes.

### The bounce arm: what the heal looks like after the roster moved underneath

`BOUNCE=1`, 240 s cut, a peer restarted gracefully at t+120 s while nothing could redistribute,
then 300 s of watching after the heal. Run directory `runs/db-partition-20260919T155256Z`; a
shorter preview run (`…T154652Z`, hold truncated by a signal — see the caveat) agreed on every
number that follows.

| | |
|---|---|
| at the bounce | `placed` **500 → 333**: exactly one node's share, 167 agents, owned by nobody. The peer shut down gracefully and deleted its node row; `wolverine_node_assignments.node_id` is `ON DELETE CASCADE`, so its assignment rows went with it |
| for the rest of the cut | **flat at 333 for 120 s.** Those agents were unassigned *and* not running, and the only actor that could place them was the isolated leader. A third of the workload was simply off |
| the replacement pod | came up and reached Ready during the cut, registered, and sat idle — the leader could not give it anything |
| on heal | 333 → 383 → 433 → **500** |
| convergence (L1) | **24.1 s** from the `db-cut-heal` mark, held 284 s (preview run: 18.9 s) |
| duplicates (S5) | **none**, across 934 harvested agent events including the bounced pod's |
| leadership | never moved. `kz6rt` held the lock and the leader row from before the cut to after the heal |

**So the heal is clean.** The leader reconnects, finds a third of the agents unowned and a node it
has never seen, and places them in about 20 s with no duplicates and no thrash. That is the answer
to "does it converge and do we get duplicates": yes, and no.

**Why no duplicates, and where they would come from instead.** The peer was stopped *gracefully*,
so it stopped its agents and removed its own claims before going — the orphans were clean, and
there was no stale ownership for anything to double up on. Leadership never changed hands either,
and leadership handover is where E2's duplicates are made. The variant that should produce them is
a **SIGKILLed** peer: its node row survives, so its assignment rows survive with it, and its agents
are left *assigned to a dead node* — the GH-3987 wedge — which the leader has to reconcile rather
than simply place. Not run.

**Read this with the stall, not instead of it.** The cluster being down a third of its agents for
the length of the partition is not a convergence failure and does not show up as one: S5, S6, S7
and L1 all pass, because at 333/333 everything placed is running and everything running is placed.
The only checks that say anything are S11 and the `placed` column of the timeline.

**Caveats.** The stall arm is one run at 180 s and the bounce arm one at 240 s (plus one truncated
preview). The **bound is not measured**: a 180 s cut was healed deliberately, so
all this shows is that the stall lasts at least as long as the partition. What eventually reaps
the server-side session is `tcp_keepalives_idle`, which defaults to 0 = the system default
(typically 7200 s on Linux), so the stranded lock could in principle outlive the partition by
hours — that needs a long-hold run to establish, and is the obvious follow-up. No duplicate agents
appeared here, but 180 s with an unfenced believer is a short window; the fencing question (E8's
fact 1) is not settled by this run either way.

The preview run `…T154652Z` was started by mistake without `DRY_RUN` and then killed by a
`timeout` at 200 s. Its cut therefore ended on a signal rather than on the hold expiring, which is
why the full-length `…T155256Z` run exists and is the one to quote. It is kept because its
sequence was intact and it agreed, and because it exposed a real defect in the script: `trap heal
EXIT INT TERM` healed the cut and then *carried on running the experiment* against an intact
cluster, since a trap handler returns rather than exits. INT and TERM now heal and stop. The same
pattern is still present in `scripts/split-brain.sh`.

## 2026-09-19 — E8 lock-session kill: 6.39.0 detects a terminated lock connection and steps down, in under a second

First run of the lock-session experiment, both arms, on the **PostgreSQL** arm. Wolverine
**6.39.0**, net10.0, `postgres:16-alpine`, 3 replicas, `SIM_AGENT_COUNT=500`,
`SIM_START_DELAY_MS=500`, stock knobs — **no** `SIM_BATCH_SIZE` (so `AgentStartBatchSize` is the
shipping default) and **no** `SIM_STABILITY_WINDOW_SECONDS` (settle gate OFF). Schema dropped and
pods bounced before the run, because the cluster had been left on `6.33.0-proposal.4`. Run
directories `runs/lock-kill-20260919T142037Z` (terminate) and `runs/lock-kill-20260919T142756Z`
(cancel).

The question was the GH-2602 shape: a leader whose lock is taken away server-side while it stays
alive, healthy and dispatching. On this build that window does not open, because the node notices.

| | |
|---|---|
| victim | pid 913, `application_name wolverine-advisory-lock:WolverineEnvelopeStorage`, from 10.244.0.15 — the leader pod, checked against the leader assignment row before firing |
| lock | advisory `832201495` = `"wolverine".GetDeterministicHashCode()` |
| the fault | `pg_terminate_backend(913)` → the injector's read-back: **released**, nothing holding it |
| handover | pid 913 granted at 14:20:58.79, pid 841 (10.244.0.14, a **different pod**) granted at 14:20:59.79 — **under one 1 s sampling tick** (K1) |
| was a peer queued on the lock? | **no.** No ungranted row for `832201495` in any sample, so this was an election, not a blocked waiter being granted |
| leaderless window | **< 1 s, and that is the instrument's floor, not a measurement of zero.** The monitor ticks at 1000 ms; resolving this properly needs `--tick-ms 100` |
| the node's own account | `warn Wolverine.Postgresql.PostgresqlMessageStore: Lost advisory-lock connection for database WolverineEnvelopeStorage; clearing held lock ids 832201495` (at `PostgresqlNodePersistence.cs:529`), then `warn NodeAgentController: Node 3 stepping down from leadership: the leadership advisory lock was released server-side. Triggering a new leadership election.` |
| checkers | all pass — S1–S7, L1 (converged 0.8 s after the marker, held 187 s), K1. S8/S9/S10/P1 skipped |
| duplicates | none (S5); `safetylab overlaps`: 0 |
| placement | 500/500 throughout, including across the handover |

**The control arm.** `pg_cancel_backend` on the then-leader's lock session (pid 841): the statement
was cancelled, and the lock **did not move for the whole 124 s** — leader unchanged, 500 placed,
no duplicates, every check green. So the lock that moved in the terminate arm moved because the
session died, not because its connection was disturbed. Without this the first arm's result would
be one observation with nothing to be an observation against.

**So: a negative result, and a specific one.** 6.39.0 handles a terminated leadership-lock
connection deliberately — it detects the broken connection, clears its held lock ids, steps down
and triggers an election rather than sitting on a stale belief. The lagging-belief window that
`GH-2602` describes, and that the `select 1` liveness ping still makes possible in principle, did
not open here at one-second resolution. What this run does **not** cover: a connection that dies
without the client noticing (a silent network drop, where TCP keepalive decides the timing rather
than an FATAL from the server) — which is the `tc netem` app↔DB partition in Layer 2, and is
materially different precisely because the detection path exercised above never fires.

**Caveats.** One run per arm. The `< 1 s` figure is bounded by the 1000 ms monitor tick. And the
first control-arm attempt (`runs/lock-kill-20260919T142442Z`) failed K1 — that was a **harness**
defect, not a finding: both arms wrote the same `lock-kill` mark, so K1 demanded the lock move on
a run designed to prove it does not. The mark now names the arm and the ledger pins all four
quadrants (`lock-kill`, `lock-kill-noop`, `lock-cancel`, `lock-cancel-moved`). That run is kept
only as the reason the fix exists.

## 2026-09-19 — E7 split brain, run 2 (store-tier cut, default client): nothing happened, and that is the result

The control for run 1, below. Same cluster, same build, same 420 s hold; the two differences are
the ones the experiment turns on. **`RAVENDB_PIN_NODE=false`** — the default RavenDB client, with
topology updates on — verified from every pod's own CONFIG line before the cut. And **only the store
member was cut**: `ravendb-0`, which `safetylab raven-cluster preferred` reported as the first node
of the database group's topology and therefore the member every un-pinned client was on, was
isolated from `ravendb-1` and `ravendb-2`; the three ChurnSim pods were not touched and could reach
every member throughout. Monitor primary view moved to `ravendb-1` for this run so the checker-facing
fields read the majority. Run directory `runs/split-brain-e7-run2-store`.

| | |
|---|---|
| the cut | 431 s (03:35:51 → 03:43:02); P1: `ravendb-0` leaderless for 427 of them |
| leader's client failover | **16 s**: last renewal on `ravendb-0` at index 10454, next committed on the majority at 03:36:07 — inside one renewal cycle |
| errors logged by the leader | **none.** Not one `Error trying to attain a leadership lock`; the failover was invisible to Wolverine |
| leadership | never lost, never split (S1, S3, S8 all pass); `ravendb-0` served its stale copy of the lock, same owner, counting down to −133 s, and was overwritten within a second of the heal |
| agent events, any pod, cut + heal | **0** — nothing started, nothing stopped |
| duplicates | none (S5); `safetylab overlaps`: 0 |
| assignment divergence | none (S9) |
| convergence after heal | 0.7 s (L1), held 313 s |
| conflicts | `CountOfConflicts` never above zero (S10). **924 resolved-conflict revisions**, exactly 308 on each of the three node-registration documents, from the heal until **5 min 12 s** after it — the same storm as run 1, same duration to the second, this time with no client having written to the isolated member at all |
| coverage | one 5.4 s monitor blind spot at 03:36:52 (C0); nothing above sits inside it |

**Read with run 1.** The pair separates two things run 1 alone could not. A partition that the
client can route around costs Wolverine on RavenDB one renewal cycle and nothing else: the default
client did exactly what it should, and "probably fine" is now measured rather than assumed. A
partition that strands a pod with its store member — the zone cut, which no client setting can
route around — is where run 1's 300 s stall and 166 duplicates live. So the finding narrows to what
the single-member entry already said, with one extension: **the five-minute compare-exchange
expiry is the RavenDB-specific issue**, and its cost under a zone partition is a live node
running agents that the majority re-places and cannot tell to stop, for as long as the stall plus
the cut lasts.

The one thing that recurred identically in both runs and is not yet explained is the
resolved-conflict storm on the three `WolverineNodes` documents: 308 revisions per document over
5 min 12 s after the heal, whether or not anyone wrote to the isolated member during the cut. No
agent-side effect either time. Worth a look at what RavenDB does with the heartbeat writes' change
vectors when a member rejoins, before deciding it is nothing.

## 2026-09-18 — E7 split brain, run 1 (zone cut): the isolated leader wrote nothing; the majority duplicated its agents for the length of the stall

First run of the network-partition experiment on the **replicated** RavenDB arm. One run; treat
every number below as an observation, not a rate. `WolverineFx.RavenDb` **6.39.0**, RavenDB server
**7.0.9** as a **three-member cluster at replication factor 3** (free Developer license), net10.0.
3 ChurnSim replicas as a StatefulSet, `churnsim-N` given `ravendb-N` as its url, `SIM_AGENT_COUNT=500`,
`SIM_START_DELAY_MS=500`, `SIM_AGENT_MB=0`, no `SIM_BATCH_SIZE`, no `SIM_STABILITY_WINDOW_SECONDS`.
Monitor at a 1 s tick reading every member; `ravendb-0` is the primary view. Run directory
`runs/split-brain-e7-run1` (48 MB, kept). minikube shape as in the single-member entry below.

**What was cut, and what it models.** Leader `churnsim-1` on `ravendb-1`. `ravendb-1` **and**
`churnsim-1` were isolated together from the other four pods for **424 s** (22:51:39 → 22:58:43),
both directions of every cross pair, via DROP rules in the node's FORWARD chain. That is a
**zone-style partition**: an application node and its nearest store member on one side, the rest
of the cluster on the other. It is not a store-tier-only partition — the pod could not reach any
majority member, so no client, pinned or not, could have routed around it.

`RAVENDB_PIN_NODE=true` was set for this run, and it matters to say what it did and did not do. It
disables the RavenDB client's topology updates, so each pod talks only to the member it was given.
It did **not** manufacture the failure mode: with the pod cut from `ravendb-0` and `ravendb-2`, a
default client would have tried them, failed, and landed on `ravendb-1` just the same. What the pin
decided was *which* pod ended up on the isolated side, by making pod-to-member affinity
deterministic — the default client sends every pod to the first node in the database group's
topology and only fails over when it cannot be reached, so without the pin the isolated pod would
have been whichever one the partition separated from that node. The pin is what makes "isolate the
leader with its member" a reproducible experiment rather than a coin flip. The store-tier-only cut,
where the pods can still reach the majority and the default client is expected to route around it,
is the control (run 2, below).

P1: `ravendb-1` reported no Raft leader for 421 of the 424 s. The monitor read all three members
throughout.

### What happened, by the clock

| t (s) | majority side (`ravendb-0`/`2`, `churnsim-0`/`2`) | minority side (`ravendb-1`, `churnsim-1`) |
|---|---|---|
| 0–299 | lock still names `churnsim-1`, TTL counting down 298 → 5, **nobody can renew or take over** | leader keeps leading on its local field; `Error trying to attain a leadership lock` every ~17 s (×16), `Error trying to perform agent health checks` ×2; **reassigns nothing** — no agent event anywhere on the cluster |
| 299 | `churnsim-0` assumes leadership the second the lock lapses; sees `churnsim-1`'s registration as stale (its health checks never left `ravendb-1`); **starts its 166 agents on `churnsim-0` (83) and `churnsim-2` (83)** | `churnsim-1` steps down at t+300: "the leadership advisory lock was released server-side" — its own copy expired. **It keeps running all 166 agents**; nothing can tell it to stop, the control queue runs through the store |
| 300–424 | 500 placed, 250/250 on two nodes; lock `churnsim-0` | `ravendb-1` still serves the **expired** lock naming `churnsim-1` (S8 fires on this member) and the old leader row; 502 assignment docs, 167 of them disagreeing with the majority (S9) |
| 424 (heal) | within 3 s the leader **deletes** the 166 rows (500 → 334 placed) and stops the 83+83 agents | still running 166 agents, now unassigned everywhere |
| 484 | the leader **re-places the same 166 on `churnsim-0`/`2`** (334 → 500) — `churnsim-1` still not counted as live | still running them: second duplicate window |
| 539 | deletes them again (→ 334), stops 83+83; `ravendb-1`'s stale documents finally agree with the primary at t+537 | `churnsim-1`'s reconcile sweep claims its 166 running agents back into the table, 50 per tick (`MaxLocalAgentReconciliationsPerTick`) |
| 551 | **converged**: 167 / 166 / 167, one leader, held 187 s to the end of the capture | |

### The findings

| | |
|---|---|
| leaderless window (majority) | **300 s** — the compare-exchange expiry, again; the majority could not take over a lock the minority could not renew |
| old leader's belief after losing its store | **300 s**, then it stepped down on its own local expiry |
| leadership split as seen by the *store* | 126 s (S1): `ravendb-1` served the expired value naming `churnsim-1` while `ravendb-0`/`2` named `churnsim-0`, until the heal |
| leadership split as *believed* by Wolverine nodes | **~1 s** — `churnsim-0` assumed at 22:56:38, `churnsim-1` stepped down at 22:56:39 |
| duplicate agents | **166 of 500**, two windows: **126 s** (takeover → heal) and **55 s** (t+484 → t+539, after the heal). 332 overlap intervals, all healed, **0 persisted** |
| assignment divergence from the minority | **none written**: the isolated leader added no claims. The 167 rows `ravendb-1` disagreed on were the *pre-cut* state it could not update (S9, 238 s, ending 115 s after the heal) |
| document conflicts | `CountOfConflicts` **never above zero** on any member at a 1 s tick (S10 passes). But `revisions/resolved` lists **1257 resolved-conflict revisions**, every one on the **three node-registration documents** (ids = the node ids), created from the heal until **5 min 12 s after it** — a health-check-cadence conflict storm on `WolverineNodes`, resolved by RavenDB's default before the monitor could see it. **No conflict on any `AgentAssignments` document.** |
| convergence after heal | 127 s (L1), held 187 s |

### What this says about the prediction

Read alongside the single-member entry below: the dominant structural finding on RavenDB is still
the **five-minute stall** — the compare-exchange lock that no one can renew and no one can take
over until it expires. Everything in this run sits downstream of it. The majority could not act for
300 s; when it finally did, the isolated node had been running unreachable for 300 s and kept
running for another 124. A shorter lease would have shrunk both windows, and the duplicates are a
consequence of the stall plus unreachability rather than a separate RavenDB replication defect.

The Layer 1c prediction — the minority leader keeps claiming agents into its own member, the
claims collide with the majority's after the heal, and the collision surfaces as document
conflicts — **did not happen**, and the reason is visible in the minority leader's log: every lock
renewal failed from 15 s into the cut, every health-check pass errored, and it changed nothing.
Whether that is because ejecting a stale node is a cluster-wide transaction that cannot commit on
the minority, or because the failing health-check loop never reached the assignment step, this run
cannot say; either way the isolated leader was **inert**, not rogue.

The duplicates came from the other side. The **majority** took the lock at expiry, treated the
isolated node as dead because its heartbeats could not replicate, and re-placed its 166 agents —
on a node that was alive, running them, and unreachable by the stop command that would have
fixed it. That is the same unfenced shape E2's handover duplicates have, arriving by a route the
reconcile sweep cannot close while the cut stands: the sweep on `churnsim-1` compared against
`ravendb-1`'s copy of the table, which still said it owned everything.

The second duplicate window is the one to look at next. For 115 s after the heal the majority
leader did not count `churnsim-1` as live, deleted and re-placed the same 166 rows twice, and it
was `churnsim-1`'s **own sweep** — GH-4407's claim-if-absent — that ended it, not the leader. And
1257 resolved conflicts on three heartbeat documents, for five minutes after a seven-minute cut,
is a cost nobody configured: neither Wolverine nor this rig sets a resolver, so RavenDB picked the
latest version 1257 times and kept a revision each time.

### Options noted, not decided

Two things the run points at, written down so they are not lost; neither is settled and neither
has been tried.

- **Self-stop on lost quorum.** The isolated node had the evidence — sixteen failed lock renewals
  from 15 s in — and acted on none of it. A node that stops its agents and releases its claims
  after N consecutive failures to prove quorum turns the duplicate window into an unavailability
  window and rejoins as an empty node, which is also the clean fix for the 115 s post-heal
  flip-flop (no stale claims to merge). The leader gets the signal for free from the lock renewal;
  a follower would need a probe — a cluster-wide no-op write, or `/cluster/topology` reporting no
  Raft leader — because on RavenDB its plain-session writes keep succeeding on its own member. The
  trade is timing: it must stop before the majority re-places, which under the 300 s expiry means
  agents running nowhere for most of that window. Does not address a stale leader that its peers
  can still reach; fencing is the complement, not the alternative.
- **Quorum writes for assignments.** Assignment turnover is rare — a handful of writes per
  handover, then nothing — so moving `AgentAssignments` from a plain session to a cluster-wide
  transaction would cost little and would close the divergence route outright: a claim that
  cannot commit on the minority is a claim that never happened, on any member. Node registration
  already uses this mode. It would not have changed run 1's duplicates (the isolated leader wrote
  nothing) but it removes the case this experiment was built to look for, and makes the heal a
  plain rejoin rather than a merge.

Testable here as written: a patched build, both hold durations, and the measurement is the
duplicate window (S5) against the running-nowhere window (S6 with the placed count).

### Caveats

- **One run.** The takeover time is structural (the expiry); the duplicate counts and the
  post-heal flip-flop may not be.
- **This is the zone cut, not the store-tier cut.** The pod was isolated with its member, so
  client failover had nowhere to go and the control queue could not reach it — which is why the
  stop commands never arrived. A partition that cuts only the store members from each other,
  with every pod still able to reach the majority, is a different experiment and is run 2.
- **The pin fixed pod-to-member affinity, nothing else.** See "What was cut" above. Without it
  the same failure mode exists for whichever pod the partition strands; with it, the stranded pod
  is the leader by construction.
- **Hold 420 s is one arm.** A hold *under* 300 s never lets the majority take over, and the two
  halves of E7 predict different behaviour there. Not yet run.
- The monitor lost 4.7 s once during the cut (C0). Nothing in the story above sits inside it.
- Conflict evidence is from `ravendb-0`'s `revisions/resolved` since the cut began; the other
  members were not asked.

## 2026-09-18 — RavenDB arm: no duplicates post-sweep, but a 5-minute leaderless stall after an ungraceful leader death

First results from the RavenDB arm. `WolverineFx.RavenDb` **6.39.0** (released, from nuget),
RavenDB server **7.0.9**, single node, net10.0. 3 replicas, `SIM_AGENT_COUNT=500`,
`SIM_START_DELAY_MS=500`, `SIM_AGENT_MB=0`, **no** `SIM_BATCH_SIZE` (so `AgentStartBatchSize` is
the shipping default) and **no** `SIM_STABILITY_WINDOW_SECONDS` (GH-4367 settle gate OFF). Every
value verified from the deployment spec before the run.

minikube under rootless podman with `--memory 8192` (enforced: `memory.max` is a real cgroup
ceiling) and CPU **unconstrained** — `--cpus` is not passed through by the rootless podman driver,
which is also true of every earlier entry here, so the CPU shape is unchanged from the PostgreSQL
runs. RavenDB is held to `RAVEN_Memory_MaxWorkingSet=1024`; see harness-traps.md for why that is
mandatory rather than tidy.

### E2 — duplicate rate: 12 / 12 clean

| | |
|---|---|
| measured rollouts | 12 |
| duplicated | **0** |
| orphaned | 0 |
| missing | 0 |
| settle | median 41 s, max 51 s |

`LeadershipAssumed` fired 11 times across 30 node starts, so the handover window that creates
duplicates on PostgreSQL was genuinely exercised — these are not zeros for want of an event.

**This is NOT a store comparison, and must not be read as one.** The PostgreSQL 7/19 figure was
taken on stock main 6.35.0 (2026-09-09). The GH-3987 reconcile sweep (#4404) and its GH-4407
hardening first shipped in **V6.36.0**, so 6.39.0 contains a fix the PostgreSQL baseline predates.
A clean result here is consistent with "the sweep works" and with "RavenDB never had it", and this
run cannot separate them. The pre-sweep RavenDB arm was deliberately not run — the sweep is merged
and the interesting question moved on.

### Leader failover — the finding

Kill the pod holding leadership and measure how long the cluster has no leader.

| | graceful (control) | **ungraceful (SIGKILL)** |
|---|---|---|
| leaderless window | 6 s | **303 s** |
| 500 agents re-placed | 28 s | 315 s |
| `NodeStopped` written | yes | **no** (73 → 73) |
| lock at t+1 s | **deleted** | held by the dead node, 299 s to expiry |

The ungraceful window is the compare-exchange lock's own `DateTimeOffset.UtcNow.AddMinutes(5)`
from `RavenDbMessageStore.Locking`, and the recovery is immediate the moment it lapses — at
t+297 s the TTL read 3 s, at t+303 s a peer held the lock with a fresh 300 s. The cluster was
never *unable* to elect a leader; it was forbidden to for five minutes.

Why no peer can shorten it: `TryAttainLeadershipLockAsync` offers a challenger two routes and the
unexpired value closes both. `PutCompareExchangeValueOperation(key, newLock, index: 0)` means
"create only if absent" and the dead leader's value is still there; `tryTakeOverIfExpiredAsync`
returns false while `ExpirationTime > UtcNow`. Nothing in the server clears it — a compare-exchange
value has no session to die with, which is exactly what makes it a good lock and a bad liveness
signal. Wolverine uses it as both.

Two details make it worse than the number suggests:

**The stall is self-sustaining.** Ejecting a stale node is the *leader's* job. With no leader, the
dead node's registration stays, so the cluster ran with four registrations for three pods. The
actor that would clean up the corpse is the one the corpse is blocking.

**The assignment set reported perfect health throughout.** `placed` read **500 of 500 for the
entire five minutes**, while ~126 of those agents were assigned to a dead node and running
nowhere. It only dropped to 374 when the new leader finally ejected the corpse, recovering to 500
twelve seconds later. Anything monitoring the assignment documents — including Wolverine's own
view of itself — would have reported a fully-placed, healthy cluster for the whole outage. This is
GH-3987's "assigned but not running" wedge arriving by a different route, and it is why this rig
reads the pod log stream rather than trusting the table.

The node that eventually took over, `fbdefcd5`, is the **restarted instance of the pod that was
killed**. It was back within seconds and then blocked by its own predecessor's lock for five
minutes.

*Caveat — the first attempt at this measured nothing.* `kubectl delete pod --force
--grace-period=0` still delivers SIGTERM, so Wolverine ran shutdown and *released* the lock; that
run produced the 6 s figure now shown above as the control arm, and is kept as
`runs/leader-kill-ravendb-INVALID-graceful/`. The SIGKILL arm asserts its own validity by counting
`NodeStopped` records across the run, since only the graceful path can write one. See
harness-traps.md.

*Not yet tested:* everything above is a **single-node** RavenDB. Wolverine writes node
registration through Raft (`TransactionMode.ClusterWide`) and the leader lock through
compare-exchange (also Raft), but writes **agent assignments through plain sessions** — single-node
writes with asynchronous multi-master replication. GH-4407's claim-if-absent guard is enforced per
RavenDB node, not cluster-wide. On a partitioned multi-node RavenDB that predicts duplicate agents
under a coherent leader, which no test here can currently reach.

## 2026-09-11 — GH-3959 collapse on net10.0: the new baseline, and the settle gate does not help

`6.35.0-cap3959.1` — `mine:gh-3987-3959-fixes` rebased onto `origin/main` @ `cd776ae32`, which
already contains the GH-3987 reconcile sweep (#4404) and its hardening (#4407). The branch adds
capacity-aware assignment and nothing else, so **all four arms below are the same image** and the
only differences are `CapacityAwareAssignment` and `AssignmentSettlePeriod`. Every setting was
verified from each pod's own `CONFIG` output. net10.0.

*Kept out of the archive because the base already contains the V6.36.0 sweep (#4404, #4407), so
the GH-3959 baseline arm still describes current Wolverine. The `CapacityAwareAssignment` arms do
not: that is a local branch, not a released package. Move this entry across if it never merged.*

Scenario unchanged: 60 agents × 10 MB ballast, 512 Mi pod limit (GC budget 384 Mi),
`SIM_START_DELAY_MS=500`, `SIM_BATCH_SIZE=5`, healthy 3-node steady state at 20/20/20, then scale
straight to **one** replica and hold it there.

| | capacity off, gate off | capacity off, gate 15 s | capacity on, gate off | capacity on + gate 15 s |
|---|---|---|---|---|
| `AssignmentChanged` in window | 777 / 6 min (**130/min**) | 741 / 6 min (**124/min**) | **1** / 7 min | **0** / 6 min |
| Converges | no | no | yes | yes |
| `OutOfMemoryException` | 2289 | ~2190 | **0** | **0** |
| Assignment rows | 36/60 | 37/60 | 19/60 + 41 waiting | 19/60 + 41 waiting |
| Agents running on survivor | 16 → **0** | **0** | **19, stable** | **19, stable** |
| Survivor load advertised | none | none | 84.0 → 77.3% | 83.2 → 80.5% |
| Pod restarts | 0 | 0 | 0 | 0 |

**The GH-4367 settle gate does not help a collapse, and structurally cannot.** 124/min against
130/min is the same number; both arms run out of memory, place nothing, and never converge. This
is not a tuning failure that a longer period would fix — `trackMembershipAndDecideWait` in
`NodeAgentController.HeartBeat.cs` ends the wait the moment it sees a departure:

```csharp
if (departed > 0)
{
    endWait();
    return false;
}
```

The gate is a **join debouncer**. It exists so a rolling deploy's arrivals do not each trigger a
rebalance, and that is the shape it was measured helping (2026-09-04, 125 → 0 stops). A node that
leaves and stays gone — maintenance, resource exhaustion, the cascading-loss endgame — is never
waited out, by design: its agents are running nowhere, and holding placement back would extend the
outage. So the gate and capacity-awareness address disjoint failure modes and neither substitutes
for the other.

*Caveat on the evidence:* the gate's own hold message is `LogDebug` and the rig logs at
Information, so its absence from the collapse logs is consistent with but not proof of the gate
never engaging. The code path above and the identical churn slopes are the actual evidence.

**The two features compose.** With both on, the collapse produced **zero** `AssignmentChanged`
rows in six minutes — marginally cleaner than capacity-alone's 1 — with the survivor holding 19
agents at a flat ~81% and no OOM. No interaction, no regression.

**Recovery** (capacity on, scale back to 3, nothing reset): rows 19 → 58 inside the first
evaluation cycle, 52 `AssignmentChanged` all in that cycle, then **flat for 5 minutes**. Final
19/20/19 at 79.2/81.6/79.8%, 58 running, 0 OOM. The old survivor kept its 19 agents — the
scale-out placed the waiting orphans rather than reshuffling what was already running.

**58/60, not 60/60, is correct.** All three nodes sit inside the 75–85 hold band, so placing the
last two would push a node to the shed line. The rig is provisioned at its edge on purpose; the
remedy is capacity or a higher threshold, not churn.

*Method note:* the headline rate is the **slope inside the observation window**, not the running
total. Totals depend on how much time elapsed between `scale --replicas=1` and the first sample,
which differed between arms and is not a property of the build.

---

## Archived

Everything measured before **V6.36.0** — the release that took the GH-3987 reconcile sweep
(#4404) and its GH-4407 hardening — is in [ARCHIVED_RESULTS.md](ARCHIVED_RESULTS.md). Those runs
provoked behaviour the fix changed, so they do not reproduce on a current package and are kept as
provenance rather than as results. That is entries dated 2026-09-10 and earlier: the stock 6.35.0
duplicate-rate and heal-test work, the mechanism trace, the 6.33 stock-vs-proposal comparison, and
the original 5.39.0 pathology.
