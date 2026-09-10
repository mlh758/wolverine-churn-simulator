# Results

Measured runs, newest first. Each entry is dated and states the build, the configuration and
the caveats it was taken under, so results accumulate here instead of being trimmed out of the
README every time a new one lands.

**Read the caveats.** A number without its configuration is not a result — `AgentStartBatchSize`
alone moved a convergence time by minutes, and a knob left set from a previous experiment is the
easiest way to publish a comparison that is really measuring something else.

## 2026-09-09 — mlh758:gh-3987-3959-fixes converges: 8 duplicates, 8 healed, 0 persisted

`6.35.0-fixes.1` — `mine:gh-3987-3959-fixes` rebased onto `origin/main` @ `c0b0ce61c`, so it
differs from the stock baseline only by its two commits. `LocalAgentReconciliationThreshold` at its
default of 3; `CapacityAwareAssignment` off, so this tests the node-side reconcile sweep alone.
8 rollouts, `scripts/heal-test.sh`, data in `runs/heal-test/`.

| | stock 6.35.0 | fixes build |
|---|---|---|
| rollouts | — | 8 |
| duplicated | ~1 in 4 | 1 |
| overlaps healed | 7 | **8** |
| overlaps **persisted** | **36** (one iteration) | **0** |

**The sweep is demonstrably what heals it**, not luck. Every overlap has a matching log line:
7 × "stopping the local copy", 1 × "restoring this node's claim" — 8 actions for 8 overlaps.

Trace of one (`sim://agent144/`):

| time | node | event |
|---|---|---|
| 23:26:20.594 | 178 | started |
| 23:26:22.401 | 179 | started — duplicate exists |
| 23:26:27.873 | 178 | `running on node 178 but its durable assignment belongs to node 179; stopping the local copy` |
| 23:26:27.874 | 178 | stopped |

5.5 s from duplicate to heal, and heal times clustered at 4.5–6.0 s across all 8 — matching
`LocalAgentReconciliationThreshold` (3) × ChurnSim's 2 s health-check almost exactly.

This is the fix working on the mechanism identified separately: duplicates are created when a new
leader cannot see agents already running on itself, so the only actor positioned to notice is the
node comparing its own running set against its durable assignments. That is what the sweep does.

**Caveats.** Only 1 of 8 rollouts duplicated, against ~23% on stock — too few runs to say anything
about the *rate*, and the fix does not claim to change it. The heal claim rests on 8 overlaps in
one rollout; what makes it more than a small sample is the per-overlap log evidence, which shows
causation rather than coincidence. Longer runs would be needed before quoting a heal rate.

## 2026-09-09 — mechanism: duplicates are created at the leadership handover

Stock `6.35.0-stock.1`, 500 agents, default batch size, GH-4367 gate off. From the run in
`evidence/dupes-2026-09-09-stock635/` (iteration 2: 537 running vs 500 assigned, 37 duplicated),
queried with `scripts/logq.sh` over the retained JSON pod logs.

**One agent's full trace** (`sim://agent114/`):

| time | node | event |
|---|---|---|
| 21:03:47.7 | 79 | started |
| 21:03:56.2 | 79 | **stopped** — clean handover, the normal path works |
| 21:04:00.0 | — | **Node 81 assumes leadership** |
| 21:04:00.9 | 81 | started |
| 21:04:02.3 | 80 | started — never stopped |

**Starts per second, by pod, across the handover.** Before 21:04:00 only `48ww4` (Node 81) is
starting agents — 19, 20, then 30 per second, absorbing ~69. At the instant it becomes leader, all
three nodes begin starting simultaneously.

**Starts vs stops over the whole window:**

| pod | starts | stops |
|---|---|---|
| `48ww4` — became leader at 21:04:00 | 179 | **0** |
| `9bgxb` | 83 | 42 |
| `hzp8p` | 110 | 42 |

The incoming leader issued 42 stops to each peer and **zero to itself**.

**The reading.** The outgoing leader had been placing agents onto `48ww4` — the only node taking
them during the rollout window. Those starts happened, but the assignment rows had not caught up.
When `48ww4` became leader and evaluated the grid, it saw those agents as *unplaced* rather than
as running-here, so it emitted plain "start on peer" commands instead of "move from here to peer".
The local copies were never told to stop, and nothing afterwards notices: the table records the
new placements and reads as a perfectly balanced 500.

This is the pending-assignment gap at a leadership handover. It matches every property observed:
duplicate pairs are always same-generation pods (not old/new drain overlap), the table always looks
immaculate, and the state never self-heals — no actor is positioned to correct it, because the
table never knew about the extra copies. A node-side reconcile sweep comparing a node's own running
set against its persisted assignments is the only place the discrepancy is visible.

**Correction to an earlier note:** extra copies do *not* reliably pile onto a single pod. That held
for the small events; in this 37-agent event the excess was spread 12 / 23 / 2.

## 2026-09-09 — duplicate rate: ~1 in 4 rolling deploys, but noisy

Stock `6.35.0-stock.1` (`origin/main` @ `c0b0ce61c`), 500 agents, 500 ms starts, 3 replicas,
GH-4367 gate off. Four runs of `scripts/duplicate-rate.sh`.

**20 duplicate events in 88 rolling deploys — ~23%.**

| run | batch size | events / deploys | rate |
|---|---|---|---|
| A | 5 | 3 / 23 | 13% |
| B | 50 (default) | 7 / 19 | 37% |
| C | 50 | 3 / 21 | 14% |
| D | 50 | 7 / 25 | 28% |

**Per-run rates are not worth comparing.** Runs C and D are the same cluster, same image, same
config, back to back — 14% then 28%, and D's first half alone was 56%. A single ~12-iteration run
gives an unstable estimate, so treat "roughly 1 in 4" as the claim and ignore differences between
individual runs.

Each iteration performs two deploys — a `rollout restart` and a `rollout.sh` — and the pre- and
post-snapshots observe both. Both are mechanically the same operation, so all are counted. Every
dirty pre-state was verified as created by that deploy: duplicates always sat on the ReplicaSet the
deploy had just made, never on the previous one.

**Duplicate size scales with batch size.** At `AgentStartBatchSize=5` no event exceeded 5 agents;
at the default 50, single events reached 22, 24 and 37. That points at the batched `StartAgents`
dispatch path rather than agents racing individually — consistent with the mechanism found
separately (see the mechanism entry above).

**Caveats.** Gate off only. Minikube, 3 replicas on one node. The rig deploys far more often than
production, which could inflate the rate if the race is sensitive to deploys in close succession.

## 2026-09-09 — first duplicate observed; GH-4367 settle gate

`6.35.0-stock.1` (`origin/main` @ `c0b0ce61c`, includes
[#4367](https://github.com/JasperFx/wolverine/pull/4367)). 500 agents, 500 ms starts, 3 replicas,
`AgentStartBatchSize=5` (not the default). Four runs: gate off, gate on, and two with the gate on.

**Where the duplicate divergence was first caught.** Five agents (`sim://agent40/`, 42, 43, 44, 46)
were started on one pod and started *again* on another 28 seconds later, with the stop for the
first copy never arriving. Verified against the live cluster ~40 minutes after the run:

| pod | running | assignment rows |
|---|---|---|
| `g7m9n` | **172** | 167 |
| `vhv9q` | 166 | 166 |
| `zjrj4` | 167 | 167 |

505 running, 500 assigned, and the assignment table evenly spread at 167/166/167 with the
duplicated agents each showing a single owner. SafetyLab's **S6 passed** — every *assigned* agent
was running where assigned, which is all the table can show — while **S5 and S7 failed**, because
they read the pod log stream. That asymmetry is the whole reason those checks exist.

**The settle gate.** One run per arm, so treat this as directional only:

| | gate off | gate on (15 s) |
|---|---|---|
| `AssignmentChanged` | 803 | 742 |
| `AgentStopped` | 247 | 66 |
| converged after rollout-end | 83 s | 44 s |

The stop count is the interesting one — stock shuffles running agents between survivors on
intermediate rosters and the gate declines to. But n=1 per arm, and the duplicate landed on a
gate-on run, so this says nothing about the gate's effect on duplication.

**Convergence is unrelated to duplication.** Convergence across the four runs was 286 s, 83 s,
44 s, 46 s; the 44 s run is the one that duplicated. The 286 s outlier was the last 29 agents going
in batches of exactly 5, exactly 40 s apart — which matches
`AgentBatchTimeouts.ReplyWindowFor(5)` (`30s + 5×1s`) plus `CheckAssignmentPeriod` of 5 s, six
times running, while the agents themselves started within ~1 s of each batch. Intermittent and
unexplained; see the separate 831 s note.

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
