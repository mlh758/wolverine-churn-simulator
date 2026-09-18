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

## 2026-09-10 — the GH-4367 settle gate does not reduce duplication

Stock `6.35.0-stock.1`, 500 agents, 500 ms starts, 3 replicas, default batch size. Two runs of
`scripts/heal-test.sh` back to back on the same cluster, gate state verified from each pod's own
`CONFIG` output. Data in `runs/heal-test-stock-gate10/` and `runs/heal-test/`.

| | gate off | gate on (`AssignmentSettlePeriod=10s`) |
|---|---|---|
| rollouts | 8 | 7 (+1 skipped) |
| duplicated | 1 (12%) | 3 (43%) |
| overlaps | 6 | 18 |
| overlaps healed | 0 | 0 |

**No evidence the gate helps.** Fisher exact p = 0.282. The point estimates run the *wrong* way,
but per-run rates on this rig have ranged 12–56% with nothing changed, so that is noise, not a
finding — do not read this as the gate making things worse.

The reasoning for expecting help was sound: the gate holds `EvaluateAssignmentsAsync` back until
the node set stops changing, and in the mechanism trace the new leader dispatched 0.9 s after
assuming leadership. Delaying that first evaluation should have let the assignment rows catch up.
It did not measurably. The race is at the handover itself, and a settle period does not close it.

**One new failure mode, gate-on only.** Iteration 7 never reached 500 placed within 20 minutes and
was skipped — a `SKIP-no-settle`, which has not appeared in any of the five gate-off runs. Possibly
related to the 831 s stall, possibly the gate holding assignment back. One occurrence; a lead, not
a claim.

**Healing, now measured on the same instrument throughout:**

| build | overlaps | healed |
|---|---|---|
| stock 6.35.0 (both gate arms) | 24 | **0** |
| `gh-3987-3959-fixes` | 8 | **8** |

Fisher exact p = 9.5e-08. Stock never heals a duplicate; the reconcile sweep heals every one, in
4.5–6.0 s, with a matching log line per overlap. That contrast is the most solid result in this
file — same script, same cluster, within hours of each other.

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

Reran on net10.0 — **see the 2026-09-11 entry**, which supersedes what was here.

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
