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
