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
`orphans.py` exists.

Open: the mechanism. Duplicate pairs are always two *new* pods from the same ReplicaSet, never an
old/new handover, so it is not drain timing. Size scaling with batch size points at the batched
`StartAgents` dispatch path rather than individual agents racing.

### Thread B — leadership algorithm misbehaviour

**The properties:** at most one node holding the lock; the lock and the leader row agreeing; the
lock never retained by a departed node; the cluster converging after a disturbance.

**Status: nothing found yet in the deploy-churn scenario.** S1–S4 have passed on every run since
the checker identifier bugs were fixed. That is a real (if unexciting) result for rolling deploys
specifically — but it is *only* rolling deploys. The scenarios most likely to break leadership are
the ones this rig does not yet inject: backend termination, app↔DB partition, process pause, clock
skew, DB failover. Those are Layer 1 and Layer 2 below.

## The layered plan

**Layer 0 — server-side invariant monitor. Built, working.**
An out-of-process observer sampling `pg_locks`, `wolverine_nodes` and `wolverine_node_assignments`
into a JSONL history, plus per-pod log harvest, plus nine offline checkers (S1–S7, L1, C0). See
the README for the property table. Everything below assumes this exists, because it is what turns
"the cluster looked fine" into a verdict.

**Layer 1 — seeded in-process nemesis harness. Not built.**
N Wolverine hosts in one process against real Postgres, a seeded random fault schedule, history
recorded via `IWolverineObserver`. Faults reachable without any infrastructure:

- `pg_terminate_backend` / `pg_cancel_backend` on the lock session
- killing the heartbeat connection *independently* of the lock connection — these can diverge and
  that state combination is untested
- stalling a node's health-check loop (simulated GC pause) → the fencing test for fact (1) above
- delaying, duplicating and reordering control-transport agent commands — highest value per line
  of code, since the receiving side has no ordering or epoch guard
- per-node clock skew → the test for fact (2) above; note `NodeAgentController.TimeProvider` is
  already an injectable internal, but the staleness comparison bypasses it and would need routing
  through it first

This is the layer most likely to find new leadership bugs, and it runs in CI.

**Layer 2 — pod-level faults in minikube. Not built.**
Only for what Layer 1 cannot fake: SIGSTOP a whole pod, `tc netem` partition pod↔Postgres
(materially different from a backend kill — the lock stays held server-side until TCP keepalive
gives up), restart Postgres under load, per-pod clock skew.

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
  actually running (replayed from each live pod's `AGENT-START`/`AGENT-STOP` via
  `running_agents.py`) versus agents assigned (the table, joined to pod name through
  `wolverine_nodes.description`). `orphans.py` diffs them. Appends to
  `runs/duplicate-rate/results.tsv` after every iteration.
- **Three divergences, kept separate:** `duplicated` (running on 2+ pods — the user-visible bug,
  and the number this experiment counts), `orphaned` (running where not assigned — the
  precursor), `missing` (assigned but running nowhere — GH-3987's wedge).
- **Finding:** any non-zero `duplicated` that survives the 120 s post-settle reconcile window.
- **Artifact:** a dirty start. Recorded as `SKIP-dirty-start`, never as a pass — and the
  pre-snapshot is *kept*, because a dirty start is itself the previous rollout's duplicate.
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

## What would change our minds

- **On thread A:** a duplicate rate of zero across 16+ clean iterations would mean the single
  observation was something rarer or environment-specific than it looks, and the honest move is to
  report it as a one-off with the run directory attached rather than as a rate.
- **On thread B:** S1–S4 continuing to pass through Layer 1's fault injection would be a genuine
  positive result about the advisory-lock design, worth saying out loud. The absence of findings
  under *rolling deploys alone* is not that result and should not be reported as one.

## Standing rules for this rig

1. **No claim without the configuration it was measured under.** A number without its knobs is not
   a result.
2. **A checker that has never been seen to fire is decoration.** `tests/selftest.sh` is the mutant
   ledger; add a fixture and a row whenever you add a check.
3. **A skipped check is not a passing one.** SKIP and PASS are different words in the report for a
   reason.
4. **Keep the raw samples.** Two identifier bugs were found in the checker itself; every affected
   run was re-checked from disk instead of re-run against the cluster.
5. **Suspect the harness first.** Of the measurement errors in this project so far, the large
   majority were the rig, not Wolverine. See [harness-traps.md](harness-traps.md).
