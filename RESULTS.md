# Results

Measured runs, newest first. Each entry is dated and states the build, the configuration and
the caveats it was taken under, so results accumulate here instead of being trimmed out of the
README every time a new one lands.

**Read the caveats.** A number without its configuration is not a result — `AgentStartBatchSize`
alone moved a convergence time by minutes, and a knob left set from a previous experiment is the
easiest way to publish a comparison that is really measuring something else.

## 2026-09-09 — duplicate-agent rate: ~1 in 4 rolling deploys

`6.35.0-stock.1` (`origin/main` @ `c0b0ce61c`), 500 agents, 500 ms starts, 3 replicas,
`maxSurge:1`/`maxUnavailable:0`, GH-4367 settle gate off. Two runs of 12 iterations,
`scripts/duplicate-rate.sh`. Raw data in `runs/duplicate-rate-batch5/` and `runs/duplicate-rate/`.

**10 duplicate events in 42 rolling deploys — ~24%.**

| | `AgentStartBatchSize=5` | `=50` (shipping default) |
|---|---|---|
| duplicate events | 3 / 23 — 13% (CI 5–32%) | 7 / 19 — **37%** (CI 19–59%) |
| duplicate sizes | 1, 5, 5 | 2, 8, 10, 11, 12, 15, 24 |
| median settle | 81 s | 40 s (one 831 s outlier) |

Each iteration performs two deploys — a `rollout restart` and a `rollout.sh` — and the pre- and
post-snapshots observe both. The two are mechanically identical operations. Every dirty pre-state
was verified as newly created by that deploy: the duplicates always sat on the ReplicaSet the
deploy had just made, never on the previous one.

**Batch size makes it worse, not better.** The rate difference is suggestive but not established
(Fisher exact p = 0.143). The size difference is clear: at batch 50 a single deploy left 24 agents
running twice, against a maximum of 5 at batch 5. The earlier 13% figure was measured on the more
favourable configuration; at defaults it is closer to 2 in 5.

**Shape.** The assignment table stayed at exactly 500 rows naming a single owner every time — the
extra copies are invisible to it, and the cluster reports itself converged. Duplicate pairs were
always two *new* pods from the same ReplicaSet, never an old/new handover overlap, so this is not
drain timing.

**Caveats.** Gate off only. n = 42 across both configs; treat "roughly 1 in 4" as the claim.
Minikube, 3 replicas on one node. The rig deploys more often than production, which could inflate
the rate if the race is sensitive to deploys in close succession.

**Why it matters.** For a Marten projection this is two daemons on one shard, indefinitely, on
default settings. A duplicate was still present on a live cluster 40 minutes after the run.

## 2026-09-09 — stock main 6.35.0, GH-4367 settle gate off vs on

**Build:** `6.35.0-stock.1`, packed from `origin/main` at `c0b0ce61c` (includes
[#4367](https://github.com/JasperFx/wolverine/pull/4367), the `AssignmentSettlePeriod` gate).
ChurnSim on net10.0. 500 agents, 500 ms start delay, 3 replicas, one rolling deploy, 10-minute
settle tail. Both arms captured with SafetyLab and identical in every respect except the gate.

**Headline: a duplicate-agent divergence that has not healed.** On the gate-on arm, five agents
(`sim://agent40/`, `42`, `43`, `44`, `46`) were started on pod `g7m9n` and then started *again*
on `zjrj4` 28 seconds later — and the stop for the first copy never came. Verified against the
live cluster ~40 minutes after the run:

| pod | agents actually running | assignment rows |
|---|---|---|
| `g7m9n` | **172** | 167 |
| `vhv9q` | 166 | 166 |
| `zjrj4` | 167 | 167 |

505 running, 500 assigned. Meanwhile the assignment table is *immaculate* — 500 rows, evenly
spread 167/166/167, `sim://agent40/` assigned to `zjrj4` alone — and the cluster reports itself
converged. This is the GH-3987 "assignment table diverges from reality" shape, and the same
duplicate-agent shape seen on 5.39 in the 2026-09-03 run: the stop for the old copy never lands,
the table claims a single owner, and nothing notices.

Note which checks caught it. **S6 passed** — every *assigned* agent was running on its assigned
node, which is all you can see by walking the assignment table. **S5 and S7 failed**, because
they read the pod log stream instead. An orphan runner is invisible to the table by construction.

**Churn:**

| | gate off (stock default) | gate on, `AssignmentSettlePeriod=15s` |
|---|---|---|
| `AssignmentChanged` | 803 | 742 |
| `AgentStarted` | 645 | 531 |
| `AgentStopped` | **247** | **66** |
| Converged after rollout-end | 83 s | 44 s |
| SafetyLab | all 9 pass | **S5, S7 fail** (5 duplicated agents) |

The gate's effect on stops is large and in the expected direction — 247 → 66, the same
"stop shuffling agents between survivors on intermediate rosters" effect the proposal showed.
Convergence was also faster with it on.

**Caveats — read these before quoting any number above.**

- **n = 1 per arm.** The duplicate appeared on the gate-on arm, but one run each cannot show the
  gate *causes* duplicates. It is at least as likely to be luck of the draw on a race that either
  arm can lose. Do not report "the settle gate causes duplicate agents" from this data.
- **`AgentStartBatchSize=5`, not the default 50.** Left set from the GH-3959 overload runs and
  resurrected by `kubectl apply`'s three-way merge after being removed. Consistent across both
  arms, so the A/B stands, but neither arm is a pristine-default baseline. It also dominates the
  convergence tail — see below.
- An earlier run in the same session was mislabelled: `kubectl set env VAR-` removals were
  silently undone by `deploy.sh`, so a run believed to be gate-off actually had the gate on. Both
  arms here verify the gate state from the pod's own `CONFIG` log line before measuring.

**Convergence tail, and where 286 s went.** An earlier gate-on run took 286 s to converge. The
capture shows it is not idle: the two survivors refill at 5 agents per ~2 s until 471 of 500 are
placed (~70 s), then the last 29 agents go in batches of exactly 5, exactly 40 s apart, for 3.5
minutes. Batch size 5 is the non-default knob above. The 40 s cadence matches
`AgentBatchTimeouts.ReplyWindowFor(5)` = `30s + 5×1s` = 35 s plus ChurnSim's
`CheckAssignmentPeriod` of 5 s, exactly, six times running — while the agents themselves start
within ~1 s of each batch. That constant's own doc comment says "the window is a backstop, not a
pace-setter"; here it is pacing. **Not proven**: that the batch acknowledgement is being lost and
the leader is burning the full window. Confirming it needs the leader's own control-plane logging,
which this capture did not collect — that is why SafetyLab now harvests
`Wolverine.Runtime.Agents.*` log lines and why a Jaeger pod is being stood up to read the
`wolverine_node_assignments` spans directly.

**Third run, with tracing and control-plane logging on** (`runs/traced`, same build and config):
681 `AssignmentChanged`, 501 started, 67 stopped, **all nine checks pass, converged in 46 s**.

Four runs were taken this session. The full record, chronological:

| # | run | gate | converged | S5 (duplicate agents) |
|---|---|---|---|---|
| 1 | `settle15-firstattempt` | on | **286 s** | pass |
| 2 | `stock-default` | off | 83 s | pass |
| 3 | `settle15` | on | 44 s | **FAIL — 5 duplicates** |
| 4 | `traced` | on | 46 s | pass |

What this does and does not support:

- **The 286 s tail is an outlier, not characteristic.** Runs 2–4 converged in 83 s, 44 s and 46 s.
  Do not quote "convergence takes ~5 minutes".
- **The duplicate divergence occurred once, in run 3, and exactly one run followed it.** That is
  not enough to call it a race that "usually" does not reproduce, and nowhere near enough to
  attribute it to the settle gate: gate-on saw 1 duplicate in 3 runs, gate-off 0 in 1 run.
- **Fast convergence and the duplicates are probably the same event, not independent ones.**
  Run 3 converged fastest *and* carried the duplicates. L1 measures convergence against the
  assignment table — one leader, 500 agents placed once, stable — and on run 3 every bit of that
  was true while five agents ran on two nodes each. The table converged quickly precisely because
  it had stopped tracking reality; the orphan copies were never in it to reconcile. This is the
  case for S5/S7 reading the pod log stream rather than the table.
- **Not settled: what paces the slow tail.** Jaeger did not answer it. Wolverine emits only three
  operations here — `wolverine_node_assignments`,
  `Wolverine.RDBMS.Transport.PollDatabaseControlQueue` and `wolverine.stopping.listener` — and the
  assignment-evaluation spans top out at **96 ms**, so the leader is demonstrably *not* blocking
  inside an evaluation. There are no send/receive spans for the agent commands, so the
  dispatch-to-confirmation gap is invisible to tracing.

  The control-plane log capture is the instrument that works. It surfaced the right line:

  ```
  warn  Node 34 confirmed 0 of 5 requested agents; 5 did not report started
        and remain unconfirmed: sim://agent475/, ...
  ```

  Batches of exactly 5 with zero confirmed — the shape the 40 s cadence predicts. But in run 4 all
  three such warnings fired within 3 ms of that pod's own "Application stopping signal received",
  so they are in-flight batches abandoned at pod termination during the rolling deploy: expected,
  and not evidence about steady-state pacing. Settling it needs a run that actually exhibits the
  slow tail with control logging on. Run 4 did not (46 s), so the capture is in place but the
  question is open.

## 2026-09-04 — stock main 6.33 vs the GH-3987/GH-3959 proposal

Builds packed into `localfeed/` from two Wolverine worktrees:
`6.33.0-stock.1` (pristine `origin/main`) and `6.33.0-proposal.*` (the
GH-3987/GH-3959 implementation).

### Churn shape — 500 agents, 500 ms starts, one rolling deploy

| | stock main | proposal (`AssignmentStabilityWindow=15s`) |
|---|---|---|
| `AssignmentChanged` for 500 agents | 501 — **1.0×, the theoretical minimum** | 501 — 1.0× |
| `AgentStarted` / `AgentStopped` | 501 / **125** | 501 / **0** |
| Churn window | ~2 min | under 1 min |
| Final distribution | 167/168/167 | 167/168/167 |
| Duplicated or missing agents | none | none |

**Main has already fixed the GH-3987 churn amplification for this scenario** —
the pending-assignment ledger (GH-3698), command batching (GH-3604/D3,
GH-3749) and the duplicate healer (GH-2602) all landed after 5.39 and account
for the 5.6× → 1.0× drop. Say that plainly before claiming anything for the
proposal.

What the proposal adds here is the 125 → 0 stops: stock shuffles running
agents between survivors on intermediate rosters, and the gate declines to.
The leader logs it — `Deferring 84 rebalancing move(s) until the cluster
topology has been stable for 00:00:15` on each mid-rollout evaluation — and
once the roster settled, **none of those 84 moves were needed at all**. No
evenness cost. What neither build addresses is divergence outside this run's
reach (raced deaths mid-evaluation, leader handover mid-deploy — see the
formal model's counterexamples).

### Cascading overload (GH-3959) — 3→1 collapse, then recovery

60 agents × 10 MB resident ballast, 512 Mi pod limit (GC budget 384 Mi, so one
node holds roughly 28 agents), paced starts. From a healthy 3-node steady
state, scale straight to **one** replica — the incident's cascading-loss
endgame — then back out to three.

**Stock main:** the survivor accepted assignments for everything, ran out of
memory starting them (`System.OutOfMemoryException`), and the leader kept
re-deciding: **~125 `AssignmentChanged` rows per minute, sustained, no
convergence across the full 5-minute observation** (655 rows total, zero
successful starts after the collapse). 39 of 60 agents held assignment rows.

**Proposal** (`SIM_CAPACITY_AWARE=true`, `SIM_OVERLOAD_THRESHOLD=85`, receive
line 75), collapse → hold → recovery in one run:

- Healthy 3-node steady state advertised **78–81%** load (~300 MB resident /
  384 MB GC budget — the arithmetic checks out).
- After the collapse the lone survivor read ~82%, inside the hold band: it
  kept its ~20 agents and **refused the 40 orphans**, which waited unassigned.
  Load held 80–83% for a 7-minute observation. **Totals: 2
  `AssignmentChanged` rows in 7 minutes** versus stock's 655 in 5 — a ~300×
  reduction — with **zero pod restarts and zero `OutOfMemoryException`**.
- **Recovery was immediate.** Scaling back to 3 replicas, assignment rows went
  22 → 61 within the first evaluation cycle after the new nodes joined: 43 of
  the run's 50 `AssignmentChanged` rows in the first minute, 7 in the second,
  zero after. Urgent placement of waiting agents is never gated. Final state
  59/60 running at 19/19/21, loads 79.5/82.5/84.4%, stable; the old survivor
  kept its agents (10 stops total).
- **The 60th agent stayed waiting — correctly.** All three nodes sit inside
  the 75–85 hold band, so placing it would push someone to the shed line. The
  design is being honest that this fleet is provisioned at its edge; the
  remedy is capacity or a higher threshold, not churn.

This is the model's cascade floor made empirical: one node's loss cannot push
the survivors past their advertised capacity, and the deficit shows up as
explicit unassigned agents rather than as a dying cluster.

| | stock main | proposal (capacity-aware) |
|---|---|---|
| `AssignmentChanged` during collapse | 655 in 5 min, ~125/min, never converges | **2 in 7 min, converged** |
| `OutOfMemoryException` | continuous | **none** |
| Survivor state | thrash loop (failed starts, releases) | steady at ~80% load |
| Agents | 39/60 rows, ~28 running, flapping | 19/60 running **stably**, 41 explicitly waiting |
| Recovery on scale-out | not reached | 40 agents placed in one cycle, 59/60 running |

### Two load-monitor lessons, kept because they cost real runs

The first capacity-aware attempts failed in the *monitor*, and both fixes are
part of the proposal:

1. `GC.GetGCMemoryInfo().MemoryLoadBytes / TotalAvailableMemoryBytes` reads
   **&gt;100% on a healthy node inside a cgroup** (page cache is counted;
   `TotalAvailableMemoryBytes` is the GC budget = 75% of the limit, verified
   empirically) and barely falls when agents stop — every node advertised
   ~112% forever and the leader shed healthy nodes to zero. The monitor now
   measures `Environment.WorkingSet / TotalAvailableMemoryBytes`.
2. Even then a saturated node **latched** overloaded: freed ballast stays in
   the GC's retained segments, so RSS does not fall on shed. Two-sided fix —
   the sim agent returns memory to the OS on stop (as a real projection
   agent's buffers would), and Wolverine gained a **10-point hysteresis
   band**: a node stops *receiving* at `threshold − 10` and starts *shedding*
   at `threshold`, so the two passes cannot oscillate around one line
   (observed before the fix: assignment rows flapping 18→29→39→10→22/min).

## 2026-09-03 — WolverineFx 5.39.0: the original pathology (historical)

Kept only as provenance for *why* the proposal exists; stock `main` is the
baseline everything below compares against. All on WolverineFx 5.39.0,
3 replicas, `maxSurge: 1` / `maxUnavailable: 0`, `minReadySeconds: 15`.

| run | agents | start delay | AssignmentChanged | amplification | settle after deploy | divergence |
|---|---|---|---|---|---|---|
| 1 | 20 | 0 | 22 | 1.1× | immediate | none |
| 2 | 200 | 250 ms | 433 | 2.17× | ~1 min | none observed |
| 3 | 500 | 500 ms | 2,821 | 5.6× | 4–5 min | 6 duplicated agents, minutes of missing/unassigned agents |

The curve is super-linear — the GH-3987 pathology needs **many agents** and
**slow starts** (projections catching up), which stretch the overlap windows
across many evaluation cycles. Extrapolated to the reporter's scale, their
24k rows for one 3-pod rollout is consistent.

Run 3's most useful finding was not the churn count: after convergence, a
cross-reference of per-pod `AGENT-START`/`AGENT-STOP` logs showed **6 agents
running concurrently on two pods** (`sim://agent477..480`, `497`, `498`). The
stop for the old copy never landed, the assignment table claimed a single
owner, and nothing in 5.39 ever noticed. That is the divergence SafetyLab's
**S5** now checks for on every run instead of by hand.
