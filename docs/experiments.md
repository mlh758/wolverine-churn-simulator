# What we are hunting, and how

## The question behind all of it

A run of GitHub issues on Wolverine looks like churn around the database lock used for leader
election — [#3987](https://github.com/JasperFx/wolverine/issues/3987) (assignment churn on every
pod replacement, agents left "assigned but not running"),
[#3959](https://github.com/JasperFx/wolverine/issues/3959) (no per-node capacity ceiling, so
redistribution cascades an overload), [#2602](https://github.com/JasperFx/wolverine/issues/2602)
(split leadership after a lock session dies), plus a series of advisory-lock bugs: stacking,
re-entrancy, connection races.

The original framing was "could we point something like Jepsen at this?" The answer we settled on:
**the Jepsen method, yes; the Jepsen framework, no.** Jepsen's value is Elle and Knossos, checkers
for transactional anomalies and register linearizability across a replicated quorum. Wolverine has
neither problem — Postgres is a single node and is already correct. Every bug in this area is
*client-side bookkeeping diverging from server-side state*. What we took from Jepsen instead:
seeded fault schedules, a timestamped history recorded out-of-band, nemeses chosen for the failure
modes that actually bite, and offline checkers as durable artifacts.

## The system under test

Leader election, as of 6.35.0:

- A **session-level PostgreSQL advisory lock**, id = `schemaName.GetDeterministicHashCode()`
  (`PostgresqlNodePersistence._lockId`). Held on one long-lived dedicated connection.
- A **heartbeat table** (`wolverine_nodes`), with stale-node ejection, hysteresis, and leader
  protection.
- An **assignment table** (`wolverine_node_assignments`), one row per agent, plus a
  `wolverine://leader/` row naming the current leader.
- Lock-loss detection is a `select 1` **liveness ping** that deliberately reports the last known
  state when its gate is busy — so "I am the leader" is a *lagging* belief, by design.

Two structural facts worth carrying:

1. **There are no fencing tokens.** `StartAgent` / `StopAgent` / `AssignAgent` carry no leader
   epoch, and the receiving node executes them unconditionally — no check that the sender still
   holds leadership. A paused-then-resumed old leader gets at least one tick of dispatch after a
   new leader exists. This is the classic Kleppmann unfenced-lock shape.
2. **Staleness detection mixes two clocks.** `health_check` is stamped by the *database*
   (`set health_check = now()`), but `staleTime` is computed from the *app node's* wall clock.
   Nothing compensates for the offset. A leader whose clock runs behind the DB by more than
   `StaleNodeTimeout` sees every peer as stale — and is the one node permitted to delete them.

Neither is currently exercised by any test, ours or Wolverine's.

### The second system under test: RavenDB

The same leader-election *protocol* runs on a structurally different lock when the store is
RavenDB (`RavenDbMessageStore.Locking`), and the differences are not incidental:

- The lock is a **compare-exchange document with a five-minute expiration**, not a session. There
  is no session whose death releases it. `tryTakeOverIfExpiredAsync` — a *peer* noticing the expiry
  and CAS-replacing the value — is the only path back to an elected leader.
- `HasLeadershipLock()` reads a local field and its expiry. It never asks the server who owns the
  key. On PostgreSQL the equivalent belief is at least refreshed by a `select 1` liveness ping;
  here there is no server round trip at all.
- The key is **renamed at runtime**: the constructor sets `wolverine/leader`, and
  `StartScheduledJobs` changes it to `wolverine/leader/<service>`. Two independent
  compare-exchange values, each with its own index, and nothing makes holding one exclude the
  other.

So the characteristic RavenDB failure is not split-brain but **stall**: after an ungraceful
leader death there is *no* leader for up to five minutes, and it is invisible from inside every
node — the dead one is dead, and the live ones see a lock they simply do not own. This is what
S8 measures, and it is the reason the arm exists. It also makes the fencing question from fact (1)
above sharper rather than softer: a leader whose lock has been taken over by a peer still believes
it holds one, and still dispatches unfenced agent commands.

Two structural facts here too:

3. **The assignment set is read through a query, not a SELECT.** It arrives with an `IsStale` flag
   and a page limit. Collection queries (`from AgentAssignments`, no filter) are served from the
   collection and cannot be stale — but a filtered one builds an auto-index that can be, and a
   short read would look exactly like a smaller, healthy cluster. The monitor stays on the
   unfiltered form and records both signals regardless; C0 fails the run on either.
4. **Staleness detection mixes two clocks here as well, and worse.** RavenDB has no `now()`, so the
   monitor reads the server's clock from the HTTP `Date` header at one-second resolution. Whatever
   the true app-versus-store skew is, this arm cannot measure it below a second — which is a real
   limit on what a RavenDB stale-node finding can claim.

## The two threads

### Thread A — duplicate agents under deploy churn

**The property:** an agent must never run on two nodes at once. This is the user-visible one; it
is what a doubled Marten projection daemon or a doubled exclusive listener looks like.

**Status: rate measured — roughly 1 in 4 rolling deploys.** 10 duplicate events in 42 deploys on
stock `main` 6.35.0 (RESULTS.md 2026-09-09). Worse at the shipping `AgentStartBatchSize=50` (7/19)
than at 5 (3/23), and duplicate size scales with batch size — up to 24 agents from one deploy.
Duplicates are permanent; one was still present on a live cluster 40 minutes later.

The shape, every time: an agent is started on one pod and started again on another, the stop for
the first never arrives, and **the assignment table stays immaculate** — 500 rows, evenly
distributed, one owner each — while the cluster reports itself converged. Any check that walks the
table sees perfect health. Only the pod log stream shows it, which is why S5/S7 read it and why
`safetylab snapshot` compares the logs against the table rather than trusting the table.

**Mechanism — found 2026-09-09, see RESULTS.md.** Duplicates are created at the *leadership
handover*, not by drain timing. The outgoing leader places agents onto the node that is about to
become leader; those starts happen before the assignment rows catch up; the new leader's first
evaluation sees them as unplaced and issues "start on peer" rather than "move from here to peer".
Evidence: the incoming leader logged 179 starts and **0 stops** on itself while issuing 42 stops to
each peer. Nothing afterwards corrects it, because the table only ever recorded the new placements
and reads as a balanced 500 — no actor knows the extra copies exist.

That makes a node-side reconcile sweep (a node comparing its own running set against its persisted
assignments) the natural fix, and it is what `mine:gh-3987-3959-fixes` adds. Testing whether it
converges rather than prevents is `scripts/heal-test.sh` — see E5.

### Thread B — leadership algorithm misbehaviour

**The properties:** at most one node holding the lock; the lock and the leader row agreeing; the
lock never retained by a departed node; the cluster converging after a disturbance.

**Status: nothing found yet in the deploy-churn scenario.** S1–S4 have passed on every run since
the checker identifier bugs were fixed. That is a real (if unexciting) result for rolling deploys
specifically — but it is *only* rolling deploys. The scenarios most likely to break leadership are
the ones this rig mostly does not yet inject: app↔DB partition, process pause, clock skew, DB
failover. Those are Layer 1 and Layer 2 below. Backend termination is the exception — it is E8,
built 2026-09-19 and described below.

## The layered plan

**Layer 0 — server-side invariant monitor. Built, working.**
An out-of-process observer sampling `pg_locks`, `wolverine_nodes` and `wolverine_node_assignments`
into a JSONL history, plus per-pod log harvest, plus nine offline checkers (S1–S7, L1, C0). See
the README for the property table. Everything below assumes this exists, because it is what turns
"the cluster looked fine" into a verdict.

**Layer 1 — seeded in-process nemesis harness. Not built.**
N Wolverine hosts in one process against real Postgres, a seeded random fault schedule, history
recorded via `IWolverineObserver`. Faults reachable without any infrastructure:

- `pg_terminate_backend` / `pg_cancel_backend` on the lock session — **now built as E8**, though
  against the real cluster rather than in-process: `scripts/lock-kill.sh`. The in-process version
  is still worth having (it is seedable and runs in CI); what E8 settles is that the fault is
  reachable and what it looks like from outside
- killing the heartbeat connection *independently* of the lock connection — these can diverge and
  that state combination is untested
- stalling a node's health-check loop (simulated GC pause) → the fencing test for fact (1) above
- delaying, duplicating and reordering control-transport agent commands — highest value per line
  of code, since the receiving side has no ordering or epoch guard
- per-node clock skew → the test for fact (2) above; note `NodeAgentController.TimeProvider` is
  already an injectable internal, but the staleness comparison bypasses it and would need routing
  through it first

This is the layer most likely to find new leadership bugs, and it runs in CI.

**Layer 1b — the RavenDB arm. Built, and it found something. (2026-09-18)**
Everything in Layer 0 now runs against RavenDB: `deploy.sh --backend ravendb`, a
compare-exchange-reading monitor, S8 on top of S1-S7/L1/C0, and the same measurement scripts.

- **E2 duplicate rate: 12/12 clean** on 6.39.0. Not a store comparison — the sweep (#4404/#4407)
  first shipped in V6.36.0, so this build contains a fix the PostgreSQL 7/19 baseline predates.
  `LeadershipAssumed` fired 11 times, so the handover window was genuinely exercised.
- **Ungraceful leader death leaves the cluster leaderless for ~5 minutes.** Measured 303 s, which
  is the compare-exchange lock's own `AddMinutes(5)`. Recovery is immediate once it lapses. See
  RESULTS.md 2026-09-18.

That second one is the "different class of problem" this arm was built to look for, and it is
structural rather than a bug: a compare-exchange value has no session to die with, which is what
makes it a good lock and a bad liveness signal. Wolverine uses it as both. Two things sharpen it:

1. **The stall is self-sustaining.** Ejecting a stale node is the leader's job, so the dead node's
   registration survives — the actor that would clean up the corpse is the one the corpse blocks.
2. **Nothing in the store shows it.** `placed` read 500/500 for the entire outage while ~126 agents
   were assigned to a dead node and running nowhere. An operator watching the assignment documents,
   or Wolverine watching itself, would have seen a healthy fully-placed cluster for five minutes.

**Layer 1c — the partition. Built and run once (2026-09-18); see E7 and RESULTS.md. The prediction below did not hold — the isolated leader wrote nothing — and the duplicates came from the majority instead.**
Everything above is a SINGLE-node RavenDB, so none of RavenDB's replication semantics were
exercised. Reading the store's source, Wolverine's writes fall into three consistency classes:

| state | mechanism | guarantee |
|---|---|---|
| leader / scheduled-job lock | compare-exchange | Raft, majority-committed, linearizable |
| node registration, agent restrictions | `TransactionMode.ClusterWide` | Raft, majority-committed |
| **agent assignments** | plain `OpenAsyncSession()` | **single-node write, async multi-master replication** |
| health checks, node records | plain session | same |

So the lock is *not* Redis-shaped — an acknowledged compare-exchange survives failover. But the
assignment set is: GH-4407's claim-if-absent guard is `StoreAsync(..., changeVector: string.Empty,
...)`, enforced by the RavenDB node serving the request, not across the cluster. Under a partition
two Wolverine nodes talking to two different RavenDB nodes can each claim the same agent, both
succeed, and the collision surfaces later as a document conflict resolved by policy rather than
prevented — and neither Wolverine nor this rig configures a conflict-resolution policy.

**Prediction:** a partitioned multi-node RavenDB produces duplicate agents under a perfectly
coherent leader — the same fingerprint the sweep fixes, arriving by a route the sweep cannot see,
because the sweep reconciles a node against *its own* assignment documents and those are exactly
what diverge. Needs a 3-node RavenDB StatefulSet at replication factor 3 and a partition between
RavenDB nodes. Untested; stated here so it is falsifiable rather than assumed.
The experiment that tests it, and the leader-election half of the same question, is E7.

**Layer 2 — pod-level faults in minikube. Partly built: the app↔store partition is E9.**
The pod↔Postgres partition is built and run — as `FORWARD` DROP rules rather than `tc netem`, the
same mechanism E7 uses — and it confirmed the parenthetical below outright: the lock stays held
server-side, for at least as long as the cut, and the node never notices. What is still not built:
SIGSTOP a whole pod, restart Postgres under load, per-pod clock skew.

*(Original note, now measured: "materially different from a backend kill — the lock stays held
server-side until TCP keepalive gives up." E8 and E9 are the two halves, and they disagree
completely.)*

**The formal models** are the third leg and already exist, on the `p-models` branch of the
`mine` fork of the Wolverine repo (the maintainer declined them upstream): P specs for
leader-election, agent-assignment and rolling-deploy, each with a mutant ledger. The intended
loop is: P finds the interleaving → Layer 1 confirms it against real Postgres → the trace becomes
a permanent regression test.

## Experiment catalogue

### E1 — churn shape (baseline vs. change)

- **Question:** does a rolling deploy cause more agent movement than the theoretical minimum?
- **Method:** 500 agents, 500 ms starts, 3 replicas, one rolling deploy. Count
  `AssignmentChanged` / `AgentStarted` / `AgentStopped` in `wolverine_node_records`.
- **Finding:** amplification well above 1.0×, or a large stop count (agents shuffled between
  survivors on intermediate rosters).
- **Artifact:** any leftover `SIM_*` knob differing between arms. `AgentStartBatchSize` alone
  moved convergence by minutes.
- **Status:** run. `main` already fixed the 5.6× amplification seen on 5.39; it now sits at ~1.0×.

### E2 — duplicate rate (`scripts/duplicate-rate.sh`) — **active**

- **Question:** how often does a rolling deploy leave an agent running on two nodes?
- **Method:** N iterations, single arm, shipping defaults. Each iteration bounces to fresh pods,
  waits for full placement to hold still, snapshots, and skips if the cluster did not start clean.
  Then one rollout, settle, snapshot again. The measurement is a direct comparison: agents
  actually running (replayed from each live pod's `AGENT-START`/`AGENT-STOP`) versus agents
  assigned (the table, joined to pod name through `wolverine_nodes.description`). Both sides, the
  diff and the verdict are one process — `safetylab snapshot` — which resolves the live pods,
  writes each raw log, reads the store on whichever arm is deployed, and prints the results.tsv
  columns. It exits **0 clean, 1 diverged, 2 could not measure**, and the script branches on that
  rather than parsing anything. Appends to `runs/duplicate-rate/results.tsv` after every
  iteration.
- **Three divergences, kept separate:** `duplicated` (running on 2+ pods — the user-visible bug,
  and the number this experiment counts), `orphaned` (running where not assigned — the
  precursor), `missing` (assigned but running nowhere — GH-3987's wedge).
- **Finding:** any non-zero `duplicated` that survives the 120 s post-settle reconcile window.
- **Artifact:** a dirty start. Recorded as `SKIP-dirty-start`, never as a pass — and the
  pre-snapshot is *kept*, because a dirty start is itself the previous rollout's duplicate.
- **Artifact:** an iteration the harness could not see. Recorded as `SKIP-unmeasurable` — an
  unreachable store, no live pod, or a pod log carrying no agent event at all. Previously these
  were indistinguishable from `clean`, which is the whole reason the row type exists.
- **Evidence:** every snapshot writes each pod's raw JSON log alongside the running/assigned diff,
  and iterations that diverge keep it (clean ones are reclaimed). A duplicate is only detected
  after the fact, and its pods are replaced by the next iteration, so without this the answer to
  *why* is gone by the time anyone looks. Query a divergent iteration straight from its snapshot:
  `./scripts/logq.sh runs/duplicate-rate/iterN/post "<sql>"`.
- **Caveat:** runs at whatever `SIM_*` knobs the deployment carries; check them. At time of
  writing `AgentStartBatchSize=5`, not the shipping default of 50, inherited from the GH-3959
  overload runs. Whether the rate holds at the default is the obvious follow-up.

**Why this design is deliberately dumb.** The first version of this experiment switched the
GH-4367 settle gate between interleaved arms and measured through the full SafetyLab capture
pipeline. Three launches produced almost no data, and every failure was its own machinery — a
`set env`/`rollout restart` race that mislabelled arms, terminating pods sampled as live, a JSON
parser that failed into an empty column. The running-vs-assigned comparison, meanwhile, found the
original duplicate *and* its first repeat without any of that. When the harness is the thing under
suspicion, prefer the measurement with the fewest moving parts.

The gate arm was dropped because the bug has since been seen with the gate both **on** (the
original observation) and **off** (a later rollout left 5 orphans), and 8 runs per arm could never
have separated the arms anyway.

### E5 — does a duplicate heal? (`scripts/heal-test.sh`)

- **Question:** given that duplication still happens, does the cluster converge back to a correct
  state or stay wrong?
- **Why a separate script:** `duplicate-rate.sh` looks once after the dust settles, which is the
  wrong instrument for a fix whose claim is convergence. `LocalAgentReconciliationThreshold`
  defaults to 3 and ChurnSim health-checks every 2 s, so a heal lands in ~6 s and an end-state
  snapshot would read clean — indistinguishable from "no duplicate happened".
- **Method:** do not sample the cluster at all. A 6 s window polled every 15 s is missed most of
  the time, and three `never` results in a row nearly became "the fix prevents duplication". Every
  AGENT-START and AGENT-STOP is already in the pod logs with a timestamp, so one capture after the
  rollout recovers the complete timeline at full resolution. `safetylab overlaps` replays it into
  residency intervals and classifies each rollout `never` / `HEALED` (with heal time) /
  `PERSISTED`, or exits 2 if it could not analyse — which is recorded as `SKIP-unanalysable`, never
  as `never`.
- **Baseline:** stock 6.35.0 is known to PERSIST — a duplicate was still live 40 minutes later.

### E6 — the synthetic-self guard (`scripts/synth-guard-run.sh`) — **blocked**

- **Question:** when a node's own snapshot omits itself while its agents are still claimed there,
  does the reconcile sweep's synthetic-self guard stop it from stopping its own agents?
- **Method:** PostgreSQL row-level security hides ONE non-leader node's row from every read the app
  role makes, while permitting all its writes — the production condition (reads lagging writes),
  made deterministic. Three deletion-based attempts failed to open the window at all. The victim's
  assignment rows are never hidden: doing that made the leader reassign its agents on ordinary
  ticks, which is churn but not the guard's domain.
- **Arm safety:** `safetylab chaos arm/disarm/status`. The disarm runs from a `trap … EXIT INT
  TERM` and is idempotent, and the run refuses to start while a previous arm is still in place. An
  arm that leaks leaves one node permanently invisible to the app, and every later experiment on
  that cluster then silently measures a crippled node.
- **BLOCKED — the instrumentation is gone.** Both arms need locally packed Wolverine builds
  (`6.35.0-sweepguard.1` / `6.35.0-sweepnoguard.1`) carrying the injection log line the whole
  measurement counts. Released **6.39.0 does not contain that string at all** — checked with
  `strings` against the deployed `WolverineFx.dll` — and those packages were deleted from
  `localfeed/` when the pre-6.36 results were archived. A run on a released build arms correctly,
  injects a real fault, and then reports `*** INVALID ARM ***` because zero injections were logged,
  which is the harness working: the guard cannot be said to have been exercised.
- **To unblock:** repack the two branch builds into `localfeed/` (README has the recipe) and
  `just deploy 6.35.0-sweepnoguard.1`, or add the marker to a current branch.

### E3 — convergence tail

- **Question:** after a rollout, how long until every agent is placed, and what is it doing?
- **Finding:** a tail that is paced rather than working. One run took 286 s, of which ~215 s was
  the last 29 agents going in batches of exactly 5, exactly 40 s apart, while the agents
  themselves started within ~1 s of each batch. 40 s = `AgentBatchTimeouts.ReplyWindowFor(5)`
  (`30s + 5×1s`) plus `CheckAssignmentPeriod` of 5 s, exactly, six times running. That constant's
  own doc comment says "the window is a backstop, not a pace-setter".
- **Status: open, and intermittent** — three later runs converged in 83 s, 44 s and 46 s. Not
  proven that the batch acknowledgement is being lost. Needs a run that exhibits the tail *with*
  control-plane logging on.
- **Note:** Jaeger does **not** answer this. Wolverine emits only `wolverine_node_assignments`,
  `Wolverine.RDBMS.Transport.PollDatabaseControlQueue` and `wolverine.stopping.listener`; the
  assignment spans top out at 96 ms, so the leader is not blocking inside an evaluation, and there
  are no send/receive spans for agent commands, so the dispatch-to-confirm gap is invisible. The
  control-plane log harvest is the instrument that works — it surfaces
  `Node N confirmed 0 of 5 requested agents`, which is the shape the 40 s cadence predicts.

### E4 — overload cascade (GH-3959)

- **Question:** when a node dies and the survivors cannot hold its share, does the cluster find a
  floor or thrash forever?
- **Status:** run against the proposal build. Stock thrashes (655 rows in 5 min, never converges);
  capacity-aware holds (2 rows in 7 min) and recovers on scale-out. See RESULTS.md 2026-09-04.

### E8 — the leader's lock session dies under it (`scripts/lock-kill.sh`) — **both arms run, 2026-09-19**

- **Question:** a PostgreSQL leader's connection goes away — it timed out, a pooler bounced, an
  operator killed the session, a blip interrupted it — and with it, instantly and silently, goes
  its leadership. How long does the node keep believing it leads, what does it dispatch while it
  does, and does the cluster come back?
- **Why it is a different experiment from leader-kill.sh.** That one kills the leader's *process*:
  the lock goes because there is nobody left to hold it, and the node that lost it is not around
  to act on a stale belief. This one leaves the node running, healthy and serving agents, and
  takes only the lock. What is under test is the gap between server-side truth and the node's
  belief, which is where both structural facts above bite at once: lock-loss detection is a
  `select 1` liveness ping that reports the last known state when its gate is busy, so the belief
  is *designed* to lag, and `StartAgent`/`StopAgent`/`AssignAgent` carry no leader epoch, so
  whatever the lagging believer dispatches is executed unconditionally. GH-2602 is this shape.
- **Method:** settle, start a capture, mark `lock-kill`, `pg_terminate_backend` the backend
  holding advisory lock `schemaName.GetDeterministicHashCode()`, watch for 180 s, capture the pod
  logs, run the checkers and `safetylab overlaps`. One row per 5 s poll to `timeline.tsv`: who
  holds the lock, who the assignment table says leads, how many agents are placed.
- **The fault is a terminated session, and it has to be.** A session-level advisory lock has no
  expiry and no owner column — the death of its session *is* its release. Nothing outside that
  session can release it: `pg_advisory_unlock(id)` only ever touches the **calling** session's own
  lock table, so from psql it releases nothing, returns `false`, and says so in a WARNING on
  stderr, where `2>/dev/null` puts it out of sight. An injector written that way runs its full
  duration, injects nothing, and leaves a plausible number behind — harness-traps.md rule 4, and
  the reason this is stated here rather than discovered later. Measured rather than assumed
  (2026-09-19, PostgreSQL 17.7): the unlock returns `f` and the lock survives; the terminate
  returns `t` and the lock is gone; a terminate aimed at a pid that has already died returns `f`
  with psql still exiting **0** under `ON_ERROR_STOP=1`, which is why the boolean is read and the
  lock is re-read afterwards.
- **The control arm is `MODE=cancel`:** `pg_cancel_backend` interrupts the connection's current
  statement and leaves the session, and so the lock, in place — verified on the same 17.7 server:
  a long-lived session sees `ERROR: canceling statement due to user request` and keeps its lock.
  If the lock moves anyway on the real cluster, that is the **client** dropping its connection in
  response to the cancellation, which is a finding about Wolverine in its own right and is
  reported as one. "A blip is not a lost lock" is what
  the default arm's finding needs to be a finding against, and the verdict logic treats the two
  modes as expecting opposite outcomes so one cannot quietly become a second copy of the other.
- **Arm safety:** there is none to have, and that is the point — a terminated session leaves no
  state behind for a later run to trip over, unlike the RLS fault (E6) or the partition (E7). What
  replaces it is proof: `safetylab lock-chaos kill` reads the lock back from the server afterwards
  and exits 2 with `*** THE FAULT DID NOT FIRE ***` unless it moved, and **K1** asserts the same
  thing against the capture — the pid holding the lock before the mark must not still hold it
  after. The two are deliberately separate: the injector can only speak for the instant it ran,
  and the run is what gets filed.
- **Refusals, each its own named reason:** a lock nobody holds (usually the wrong lock id — the
  `LeaderLockId = 9999999` constant sits in the same class as `_lockId` and is unused by the
  leadership path), a backend merely *queued* on the lock, two granted holders at once
  (impossible in PostgreSQL, so the filter is wrong and killing either would hit an unrelated
  session), a holder with no `client_addr`, and a holder whose address is not the leader pod's —
  that last is the state this experiment exists to create, so a cluster already in it is not a
  starting point.
- **Finding:** a node logging leadership work, or dispatching agent commands, after the lock left
  it; a leader row naming a node with no lock behind it for longer than the grace window (S3); a
  cluster that does not re-elect at all (L1, K1's note about nobody taking the lock); duplicate
  agents in the post-kill logs. A cluster that notices within a health-check period and re-attains
  cleanly is a real negative result — say so if that is what happens.
- **Result (2026-09-19): that negative, and it is worth having.** 6.39.0 detects the terminated
  connection itself — `Lost advisory-lock connection … clearing held lock ids 832201495`
  (`PostgresqlNodePersistence.cs:529`) — steps down, and triggers an election; a peer held the
  lock inside one 1 s sampling tick, with no waiter ever queued on it. Every check green, no
  duplicates, 500/500 placed throughout. The control arm's lock did not move for 124 s, so the
  terminate arm's handover is attributable to the session dying rather than to its connection
  being disturbed. See RESULTS.md.
- **What it does NOT cover, and is the obvious next run.** The detection path above fires because
  the server sends the client a FATAL and the connection breaks *visibly*. A connection that dies
  where the client cannot tell — a silent drop, TCP keepalive deciding the timing — never reaches
  it, and that is exactly the app↔DB partition in Layer 2. E8's result makes that experiment more
  interesting rather than less.
- **Resolution limit.** The monitor ticks at 1000 ms (`k8s/safetylab.yaml`), so "the lock was
  unheld for 0.0 s" means *below one tick*, never zero. The leaderless window is the number this
  experiment exists to produce, so a rerun at `--tick-ms 100` is the way to actually produce it.
- **Artifact:** a lock that moved for a reason other than the fault. The node may have been mid-
  handover, or the pod may have restarted; the timeline and the capture both name pods, and K1's
  notes say which pid took the lock next.

### E9 — the leader loses its database, not its lock (`scripts/db-partition.sh`) — **run 2026-09-19, and it found something**

- **Question:** E8 killed the lock *session* and the client found out immediately. What happens
  when the connection dies where the client cannot tell — a partition, a hung store, a saturated
  link? The lock is not released, because nothing released it: the session is alive on the server
  and it is the client that cannot reach it.
- **Method:** `just db-partition [hold] [settle]`. Resolve the leader through
  `safetylab lock-chaos status` (so "the leader" means the node whose address holds the advisory
  lock), cut its pod from the `pg` pod both ways with `safetylab partition arm`, hold, heal from a
  trap, watch, and check. The monitor and every psql read sit in other pods on the database side
  of the cut, so the observer keeps full sight of the lock throughout.
- **Marks and proof:** `db-cut-start` / `db-cut-heal`, deliberately NOT E7's `partition-start` —
  that fault is proved by a RavenDB member losing its Raft leader, and there is no Raft here.
  **K2** proves this one from the database side instead: a node that cannot reach the store stops
  advancing its `health_check` while its peers keep going, which is visible from outside with no
  cooperation from the cut-off node.
- **Finding (2026-09-19): the lock strands and nothing notices.** 180 s cut; the node stopped
  heartbeating for 179 s of it; the advisory lock never moved off its session; the leader row never
  changed; the node was never ejected (the actor that ejects stale registrations is the leader, and
  the leader is the one cut off); `placed` read 500/500 throughout. The node logged
  `Error writing the heartbeat` ten times and **zero** step-downs — against E8's build, on the same
  cluster, where the terminate arm produced a step-down immediately. Recovery on heal was 0.3 s.
  See RESULTS.md.
- **So the detection path is connection-break-shaped.** 6.39.0 handles a FATAL from the server
  well and does not act on "my writes keep failing". Those are the same event to an operator.
- **Why S11 had to exist.** S4 cannot see this (the node is never *departed*), S1/S2/S3 cannot (the
  lock and the row agree — they both name a node that is not there), and L1 cannot (one holder,
  everything placed, nothing dangling: a stalled cluster satisfies every clause of convergence).
  Before S11 this run produced a fully green report over a three-minute leaderless stall.
- **Artifact:** the victim's pod restarting during the hold. It comes back on a new IP, outside
  every rule, and has escaped the cut for the rest of the run; the script counts restarts before
  and after and prints `*** SUSPECT RUN ***`.
- **The bounce arm (`BOUNCE=1`), and the question it answers.** A stall on a CP-flavoured system is
  defensible behaviour rather than a bug; what is not obvious is what happens on the *heal*, after
  the roster moved while nobody could act. So this arm restarts a non-isolated peer at the midpoint
  of the hold. The peer stops gracefully, deletes its node row, and `ON DELETE CASCADE` takes its
  assignment rows with it — so its agents are unassigned AND not running, and only the isolated
  leader could place them. **Measured 2026-09-19:** `placed` dropped 500 → 333 and stayed there for
  the rest of the cut, then recovered to 500 in **24 s** after the heal, with **no duplicates**
  (S5) and no change of leadership. The heal is clean.
- **Which instrument answers the duplicate half.** Not `safetylab overlaps` alone — it reads raw
  pod logs from `post/`, which only holds pods still alive at the end, and the bounced pod is by
  definition gone. **S5** covers it, because it builds residencies from the capture's harvested
  AGENT-START/STOP stream, which the monitor's follower collected from the bounced pod while it was
  still running. `overlaps` stays as a cross-check over the survivors.
- **Why no duplicates, and the arm that should produce them.** A graceful stop removes the peer's
  claims before it goes, so the orphans are clean and there is no stale ownership to double up on;
  leadership never moves either, and handover is where E2's duplicates are made. A **SIGKILLed**
  peer is the variant to try: its node row survives, so its assignment rows survive, and its agents
  are left *assigned to a dead node* — the GH-3987 wedge — for the leader to reconcile rather than
  simply place. Not run.
- **A third of the workload can be off without any check saying so.** At 333/333 everything placed
  is running and everything running is placed, so S5/S6/S7 and L1 all pass. Only S11 and the
  `placed` column of the timeline show it. Worth knowing before reading a green report.
- **Open, and the obvious next run: the bound.** The 180 s hold was healed deliberately, so the
  stall is only known to last *at least* as long as the partition. What eventually reaps the
  server-side session is `tcp_keepalives_idle`, which defaults to 0 = the system default
  (typically 7200 s), so the stranded lock may outlive the cut by hours. A long-hold run would
  establish it. The fencing question is also unsettled: 180 s produced no duplicates, but that is
  a short window for an unfenced believer.

### E7 — split brain on a RavenDB cluster (`scripts/split-brain.sh`) — **run twice, 2026-09-18/19**

- **Question:** during a network partition between RavenDB nodes, what do leader election and
  agent assignment actually do? Two halves, because the store treats them differently:
  1. **Leadership.** The lock is compare-exchange, so the *server* cannot hand it to two owners —
     the minority side simply cannot write it. But `HasLeadershipLock()` never asks the server.
     A leader whose RavenDB node lands in the minority keeps believing it leads, on a local field,
     for up to the five-minute expiry, while the majority side is free to take the lock over once
     it lapses. The question is how long two nodes *believe* they lead at once, what each of them
     dispatches while it does, and how quickly the stale one notices when the partition heals.
  2. **Assignments.** Plain sessions, single-node writes, async replication. Two leaders — or one
     leader and one stale believer — writing claim-if-absent assignments to different RavenDB
     nodes should both succeed, and the collision should surface after the heal as a document
     conflict rather than a refused write. That is the Layer 1c prediction: duplicate agents by a
     route the reconcile sweep cannot see.
- **Method:** `just deploy-ravendb-cluster`, then `just split-brain [hold] [settle]`. A 3-member
  RavenDB StatefulSet at replication factor 3 (`k8s/ravendb-cluster.yaml`), ChurnSim as a
  StatefulSet with `churnsim-N` pinned to `ravendb-N` (`k8s/churnsim-ravendb-cluster.yaml`), and a
  partition that isolates one member *together with its ChurnSim pod* from the other two. Pinning
  is `RAVENDB_PIN_NODE=true`, which sets `DisableTopologyUpdates` on the RavenDB client: otherwise
  it fetches the group topology and fails over to any reachable member, cutting only the
  store-to-store links makes every Wolverine node quietly reroute to the majority, and there is no
  split to observe — the run measures client failover instead. The script settles the cluster,
  reads the leader, hands leadership off gracefully while it sits on `ravendb-0` (the monitor's
  primary view has to stay on the majority side), and then the leader's member is the minority:
  no need to kill anything to get the leader onto the isolated side, because the side is chosen
  around the leader. Default hold is 420 s, past the 300 s lock expiry so the majority gets to
  take the lock over while the old leader still believes; a hold under 300 s is the other arm
  and worth running too, because the two halves above predict different behaviour on either
  side of that line.
- **The cut:** `safetylab partition arm|heal|status`. `DROP` rules in the minikube node's `FORWARD`
  chain, keyed on pod IPs, both directions of every cross pair — minikube's `ptp` CNI forwards
  pod-to-pod traffic through the host kernel, so this cuts exactly the named pairs and touches no
  pod. Each rule carries a comment so `heal` deletes exactly what `arm` added and none of
  kube-proxy's rules in the same chain; `arm` reads the rules back and refuses a partial arm;
  `heal` runs from a trap and refuses to report success while any rule remains; a run refuses to
  start over a cut a previous run left behind. Verified live 2026-09-18 with a TCP probe from the
  isolated member: reachable, unreachable while armed, reachable after the heal.
- **Instrument:** the monitor samples *every* member, not the Service (`k8s/safetylab-ravendb-
  cluster.yaml`), because during a partition the answer differs by member and the whole finding is
  in that difference. Every sample carries the primary's view in the usual fields plus a
  `ReplicaView` per member: its Raft view (`/cluster/topology`), its `CountOfConflicts`, and the
  assignment rows on which it disagrees with the primary, stored as a diff rather than a second
  copy. Compare-exchange rows are tagged with their source member, so S1 is a check *across*
  members — agreeing members collapse to one holder, disagreeing ones are two. Three checkers
  exist for this arm alone: **S9** (a member holding assignments the primary does not, past the
  grace window), **S10** (any member's conflict count above zero, zero grace), and **P1** (the
  nemesis must prove it fired: between the `partition-start` and `partition-heal` marks at least
  one member must have reported losing its Raft leader). The pod logs carry the belief side —
  `LeadershipAssumed` and every `AGENT-START`/`AGENT-STOP` — so `safetylab overlaps` gives the
  duplicate timeline exactly as in E5, and `safetylab query conflicts` reads RavenDB's current
  conflicts *and* its resolved-conflict revisions after the heal, because neither Wolverine nor
  this rig sets a conflict-resolution policy and whatever the default does is part of the result.
  The mutant ledger has four fixtures for this: a healthy three-member read (one leader, S9/S10
  quiet), the predicted split (S1 + S9), a post-heal conflict (S10), and a partition that never
  took (P1).
- **Finding:** two nodes logging leadership and dispatching agent commands over the same interval;
  assignment documents in conflict after the heal, or silently resolved to one side while the
  other side's agents keep running; the stale believer surviving past the heal by more than a
  health-check period. A minority-side leader that stops dispatching within a tick of losing the
  store, and a heal that leaves zero conflicts and zero duplicates, would be a real negative
  result — say so if it happens.
- **Artifact:** a monitor or a ChurnSim pod on the wrong side of the cut. The rig's own partition
  is the first suspect for any "nobody led for N minutes" reading; record which side each pod and
  the monitor were on, in the run directory, before reading the checkers.
- **Artifact:** client failover masquerading as a split (see Method). If every node's log shows
  the same store URL throughout, the partition did not hold on the client side.
- **Caveat:** everything measured so far on the RavenDB arm is single-node, so none of the
  replication behaviour this depends on has been exercised even once. Expect the first runs to
  be about the rig — StatefulSet bootstrap, database creation racing across three nodes
  ([harness-traps.md](harness-traps.md) already records the single-node version of that race).
- **Two cuts, one knob.** `CUT=zone` (default) isolates the leader's store member *and* the
  leader's pod — a zone-level partition, which no client can route around; it needs
  `RAVENDB_PIN_NODE=true` so that "the leader's member" is a fact and the stranded pod is the
  leader by construction. `CUT=store` isolates only the member the default client prefers (the
  first node of the database group's topology, which every un-pinned client is on) and leaves the
  pods alone — a store-tier partition, which asks what the default client does. Each verifies the
  pin from the pods' own CONFIG lines before doing anything.
- **Status: run once per cut (RESULTS.md, 2026-09-18 and -19).** *Zone cut:* the prediction's
  assignment half did not hold — the isolated leader's every lock renewal and health-check pass
  failed and it **wrote nothing**, stepping down on its own local expiry at t+300, one second
  after the majority took the lock. Belief split ~1 s; store-level split (S1, the minority member
  serving the expired key) 126 s. The duplicates came from the **majority**, which treated the
  isolated live node as dead at takeover and re-placed its 166 agents onto nodes that ran them
  alongside it for 126 s, plus a second 55 s window after the heal while the leader flip-flopped
  on whether the node was live. 0 persisted. *Store cut, default client:* the leader failed over
  in 16 s inside one renewal cycle, logged no error, and **nothing happened** — no agent event,
  no split, convergence 0.7 s after the heal. So the RavenDB-specific issue is the five-minute
  expiry, and its cost under a zone partition is a live node whose agents the majority re-places
  and cannot tell to stop. Both runs left an identical, unexplained storm of resolved-conflict
  revisions on the three node-registration documents for 5 min 12 s after the heal (308 per
  document), with no agent-side effect. **Next:** the sub-300 s zone hold, a repeat of the zone
  cut for the post-heal flip-flop, and a look at the heartbeat conflict storm.

## What would change our minds

- **On thread A:** a duplicate rate of zero across 16+ clean iterations would mean the single
  observation was something rarer or environment-specific than it looks, and the honest move is to
  report it as a one-off with the run directory attached rather than as a rate.
- **On thread B:** S1–S4 continuing to pass through Layer 1's fault injection would be a genuine
  positive result about the advisory-lock design, worth saying out loud. The absence of findings
  under *rolling deploys alone* is not that result and should not be reported as one.
- **On the RavenDB partition (E7):** a stale leader that stops dispatching as soon as its store
  is unreachable, and assignments that never conflict after a heal, would mean the consistency
  classes in Layer 1c are not the failure they read as — either the client is doing more than
  the source suggests or the sweep catches it after all. That would be worth as much as the
  prediction coming true. *Run 1 landed here on the assignment half:* the isolated leader wrote
  nothing and no assignment document ever conflicted. What it did not do is make the partition
  safe — the majority duplicated the isolated node's agents for three minutes across two windows.
  The mind-changing result now would be a repeat where the majority's takeover does **not**
  re-place a live-but-unreachable node's agents, or a sub-300 s hold that produces no duplicate
  at all.

## Standing rules for this rig

1. **No claim without the configuration it was measured under.** A number without its knobs is not
   a result — and that now includes the backend. Two arms exist; a figure filed under the wrong
   one is worse than no figure, which is why `SIM_BACKEND` is asserted by the app at startup and
   read off the deployment (never passed as an argument) by every measurement script.
2. **A checker that has never been seen to fire is decoration.** `tests/selftest.sh` is the mutant
   ledger; add a fixture and a row whenever you add a check.
3. **A skipped check is not a passing one.** SKIP and PASS are different words in the report for a
   reason.
4. **Keep the raw samples.** Two identifier bugs were found in the checker itself; every affected
   run was re-checked from disk instead of re-run against the cluster.
5. **Suspect the harness first.** Of the measurement errors in this project so far, the large
   majority were the rig, not Wolverine. See [harness-traps.md](harness-traps.md).
