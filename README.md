# wolverine-deploy-sim

A minimal, reproducible simulation of Wolverine's agent-assignment churn during
Kubernetes rolling deploys — the behavior reported in
[JasperFx/wolverine#3987](https://github.com/JasperFx/wolverine/issues/3987)
(assignment churn on every pod replacement, agents left "assigned but not
running") and its overload sibling
[#3959](https://github.com/JasperFx/wolverine/issues/3959) (no per-node
capacity ceiling, so redistribution can cascade an overload).

The companion formal model for the proposed fix lives in the Wolverine repo at
`formal/rolling-deploy/` (P model checker); this project is the empirical half:
reproduce the churn on stock Wolverine first, then rebuild with the proposal
and measure again.

## What it is

- **`src/ChurnSim`** — a .NET 10 console host using released
  `WolverineFx.Postgresql` (5.39.0) with PostgreSQL-backed durability. It
  registers a custom `IStaticAgentFamily` (`sim://agent1..N`) whose
  `EvaluateAssignmentsAsync` calls `AssignmentGrid.DistributeEvenly` — the
  same distribution the Marten projection/subscription agents use, so the
  assignment plane behaves exactly like a production critter-stack app with
  N projections. Each agent logs `AGENT-START` / `AGENT-STOP`; an optional
  `SIM_AGENT_MB` knob gives each running agent a real memory footprint for
  overload (GH-3959) scenarios.
- **`k8s/`** — a single-pod PostgreSQL and a 3-replica `churnsim` Deployment
  with a production-shaped rolling update (`maxSurge: 1`,
  `maxUnavailable: 0`, `minReadySeconds: 15`) so old pods drain while new
  pods have already joined the Wolverine cluster.
- **`scripts/`** — build/deploy, trigger a rolling deploy, and measure churn
  from the `wolverine_node_records` table plus pod logs.

## Environment setup (one time)

Podman must be installed (the native CLI, not the Desktop flatpak). Then:

```bash
# minikube as a plain user-space binary
curl -sLo ~/.local/bin/minikube \
  https://storage.googleapis.com/minikube/releases/latest/minikube-linux-amd64
chmod +x ~/.local/bin/minikube

# rootless podman driver
minikube config set rootless true
minikube start --driver=podman --container-runtime=containerd --cpus=4 --memory=6g

# kubectl comes along for free
minikube kubectl -- get nodes
```

ChurnSim and the SafetyLab monitor both build inside
`mcr.microsoft.com/dotnet/sdk:10.0` images, so no local SDK is needed to run
the cluster. `safetylab check` does run on the host — `nix develop` provides
the .NET 10 SDK for it, along with kubectl, podman and jq.

> ⚠️ **kubectl context**: every script pins `--context=minikube` explicitly.
> Do not run bare `kubectl apply` from this repo — if your default kubeconfig
> context points at a real cluster), that is
> where the manifests would land. Keep the pin if you edit the scripts.

## Running the baseline (reproduce GH-3987)

```bash
./scripts/deploy.sh          # build image, load into minikube, deploy pg + 3 replicas
./scripts/reset-metrics.sh   # zero the node_records history once the cluster is settled
./scripts/rollout.sh         # one rolling deploy: 3 pods replaced one at a time
./scripts/measure.sh         # churn report
```

`measure.sh` reports:

- `wolverine_node_records` counts by event type — `AssignmentChanged` is the
  churn signal the issue reporter counted (24k rows for one 3-pod rollout in
  their production system);
- `AssignmentChanged` rows per minute, to see the churn concentrated in the
  rollout window;
- current node registrations and per-node agent assignment counts — after the
  cluster settles, every `sim://` agent should have exactly one assignment on
  a live node. Discrepancies between `wolverine_node_assignments` and what
  pods actually run are the "assigned but not running" symptom;
- `AGENT-START` / `AGENT-STOP` counts from live pod logs (note: logs of
  replaced pods are gone, so the durable node_records numbers are the source
  of truth).

To measure a steady-state baseline (no deploy), reset metrics, wait a few
minutes, and measure again — a healthy cluster should write ~zero
`AssignmentChanged` rows at rest.

## SafetyLab — server-side invariant checking

`measure.sh` counts churn. SafetyLab answers the different question: **did the cluster ever
violate a safety property, and did it converge afterwards?**

It watches from outside. Every existing check on this machinery is an in-process assertion
inside the node under test, so it can only confirm what that node *believes* — and the bugs
here are exactly where belief and server disagree. In
[GH-2602](https://github.com/JasperFx/wolverine/issues/2602) a leader's Postgres backend was
terminated, its in-process lock list went on reporting "held", and two nodes both led. From
`pg_locks` that is one row.

### What it checks

**S** = safety, must never happen. **L** = liveness, must eventually happen.
**C** = coverage, how much was actually observed.

| | Property | Fails when |
|---|---|---|
| S1 | At most one backend holds the leader advisory lock | sentinel — if it trips, the lock id is wrong and every leader check below is vacuous |
| S2 | At most one `wolverine://leader` assignment row | sentinel — already PK-enforced |
| S3 | The lock and the leader assignment row agree | sustained split between "who holds the lock" and "who owns the row" |
| S4 | The lock is not held by a departed node | the stacking / failover-stall fingerprint |
| S5 | **No agent runs on two nodes at once** | the user-visible property: a doubled projection daemon or exclusive listener |
| S6 | Every assigned agent is running on its assigned node | GH-3987's "assigned but not running" wedge |
| S7 | Every running agent is assigned to the node running it | an orphan runner — the precursor to S5 |
| L1 | The cluster converges after the last phase marker | one leader, every agent placed once, and it stays that way |
| C0 | Observation coverage | sampling gaps, failed samples, monitor↔database clock offset |

S3–S7 are grace-windowed (`--grace`, default 15s): handover is not atomic, so a tick or two
of disagreement is the protocol working. What gets reported is divergence that does not end.

### Running it

```bash
nix develop                              # dotnet 10 + kubectl + podman + jq
./scripts/monitor.sh deploy              # build + deploy the in-cluster monitor (once)

./scripts/monitor.sh start rollout-1     # begin capturing BEFORE the disturbance
./scripts/rollout.sh
./scripts/monitor.sh mark rollout-end
sleep 120                                # let it settle, so L1 has a tail to judge
./scripts/monitor.sh stop
./scripts/monitor.sh check rollout-1
```

A run directory (`runs/<name>/`) is a durable artifact and stays checkable after the cluster
is gone: `history.jsonl` (server-side samples), `pods.<pod>.jsonl` (node identities,
`AGENT-START`/`AGENT-STOP` residencies, and `Wolverine.Runtime.Agents.*` control-plane log lines),
`marks.jsonl` (phase boundaries). `check --json` emits the same results for scripting; exit code
is 1 if anything failed.

That the raw samples are kept, rather than just verdicts, is the point: when two identifier bugs
were found in the checker itself, every affected run was simply re-checked from disk instead of
re-run against the cluster.

### Two gotchas

**Start the capture before the rollout.** `kubectl logs` cannot reach a pod once it is gone,
and during a rolling deploy the pods that matter most are exactly the ones that disappear. A
pod replaced while nothing was following it contributes no residencies, which silently
weakens S5/S6/S7. This is why C0 exists and why a skipped check reports SKIP, not PASS.

**A checker that has never been seen to fire is decoration.** `tests/selftest.sh` is the
mutant ledger: seven synthetic fixtures, each injecting one fault, each declaring which
checks must go red — and that the others stay green.

```
$ nix develop --command ./tests/selftest.sh
  ok   clean          -> []
  ok   dup-agent      -> [S5 S7]
  ok   orphan-lock    -> [L1 S3 S4]
  ok   stranded       -> [S6]
  ok   no-converge    -> [L1]
  ok   split-leader   -> [S3]
  ok   gap            -> [C0]
```

Add a checker, add a fixture and a ledger row.

## Tracing (Jaeger)

State samples show *what* the assignment table did; they never say *why*. Wolverine publishes a
`Wolverine` ActivitySource and wraps every assignment evaluation in a `wolverine_node_assignments`
span, and agent commands ride the ordinary message pipeline — so a trace shows the leader's
dispatch, the receiving node's execution, and the gap between them. No Wolverine code or
configuration changes are needed: health-check tracing is on by default and unthrottled.

```bash
kubectl apply -f k8s/jaeger.yaml
kubectl set env deployment/churnsim SIM_OTLP_ENDPOINT=http://jaeger:4317
./scripts/traces.sh 30          # span durations + percentiles for the last 30 min
kubectl port-forward svc/jaeger 16686:16686   # then browse localhost:16686
```

ChurnSim only wires up OpenTelemetry when `SIM_OTLP_ENDPOINT` is set, so a run without it stays
byte-for-byte comparable with earlier results.

## Knobs

| Knob | Where | Default | Meaning |
|---|---|---|---|
| `SIM_AGENT_COUNT` | `k8s/churnsim.yaml` | 20 | number of `sim://` agents to distribute |
| `SIM_AGENT_MB` | `k8s/churnsim.yaml` | 0 | resident MB each *running* agent allocates (for GH-3959 overload runs) |
| `replicas` | `k8s/churnsim.yaml` | 3 | cluster size |
| memory `limits` | `k8s/churnsim.yaml` | 512Mi | the OOM kill-line for overload scenarios |
| `HealthCheckPollingTime` / `CheckAssignmentPeriod` | `Program.cs` | 2s / 5s | tightened from 10s/30s so a short rollout spans several control-plane cycles |

## Why this exists

**[docs/experiments.md](docs/experiments.md)** is the intent: what we are hunting, the experiment
catalogue, what counts as a finding versus an artifact, and what is currently open.
**[docs/harness-traps.md](docs/harness-traps.md)** is the list of ways this rig has produced
confident wrong answers — read it before trusting a number.

## Results

Measured runs live in **[RESULTS.md](RESULTS.md)**, dated newest first — 5.39's original
pathology, stock `main` versus the proposal on churn shape, and the GH-3959 overload
collapse-and-recovery.

## Reproducing the phase 2 runs

The proposal implementation lives on the Wolverine branch
`gh-3987-3959-assignment` (three commits off `main`): the
`AssignmentStabilityWindow` gate, the node-side assigned-vs-running
reconciliation sweep, and capacity-aware assignment (WorkingSet-based
`INodeLoadMonitor`, `load_factor` heartbeat column on PostgreSQL, hysteresis
band, shed pass, waiting state). The formally verified design it follows is
`formal/rolling-deploy/README.md` in the Wolverine repo.

`localfeed/` is gitignored; regenerate the packages from two Wolverine
worktrees, then deploy by version:

```bash
# stock baseline from pristine main
git -C ~/GitHub/wolverine worktree add ../wolverine-stock --detach origin/main
for p in src/Wolverine/Wolverine.csproj \
         src/Persistence/Wolverine.RDBMS/Wolverine.RDBMS.csproj \
         src/Persistence/Wolverine.Postgresql/Wolverine.Postgresql.csproj \
         src/Wolverine.RuntimeCompilation/Wolverine.RuntimeCompilation.csproj; do
  (cd ~/GitHub/wolverine-stock && dotnet pack $p -p:Version=6.33.0-stock.1 -o ~/GitHub/wolverine-deploy-sim/localfeed)
  (cd ~/GitHub/wolverine-proposal && dotnet pack $p -p:Version=6.33.0-proposal.3 -o ~/GitHub/wolverine-deploy-sim/localfeed)
done

./scripts/deploy.sh 6.33.0-stock.1       # or 6.33.0-proposal.3
```

Churn-shape runs: `reset-metrics.sh` → `rollout.sh <stamp>` → settle →
`measure.sh`; for the proposal add
`kubectl set env deployment/churnsim SIM_STABILITY_WINDOW_SECONDS=15`. Capture
the same window with `monitor.sh` to get the safety verdict alongside the
churn count.

Overload runs: `kubectl set env deployment/churnsim
SIM_AGENT_COUNT=60 SIM_AGENT_MB=10 SIM_START_DELAY_MS=500 SIM_BATCH_SIZE=5`
(plus, for the proposal, `SIM_CAPACITY_AWARE=true SIM_OVERLOAD_THRESHOLD=85`),
settle, `reset-metrics.sh`, then `kubectl scale deployment/churnsim
--replicas=1` and watch `wolverine_node_records`, `wolverine_node_assignments`
and the `load_factor` column for the observation window. Use
`reset-schema.sh` between different Wolverine builds. For the recovery half,
scale back to `--replicas=3` without resetting anything and keep watching.

> The published numbers above were taken with ChurnSim on net9.0. It targets
> **net10.0** now (which also floats `Microsoft.Extensions.Hosting` to 10.0.0,
> since WolverineFx's own floor moved). The assignment plane does not depend on
> the target framework, so the comparisons are expected to hold — but a rerun
> has not been done, so treat any new absolute number as a fresh baseline
> rather than something directly comparable to a row above.
