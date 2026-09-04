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

- **`src/ChurnSim`** — a .NET 9 console host using released
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

No local .NET SDK is needed — the app builds inside the
`mcr.microsoft.com/dotnet/sdk:9.0` image.

> ⚠️ **kubectl context**: every script pins `--context=minikube` explicitly.
> Do not run bare `kubectl apply` from this repo — if your default kubeconfig
> context points at a real cluster (e.g. a production GKE context), that is
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

## Knobs

| Knob | Where | Default | Meaning |
|---|---|---|---|
| `SIM_AGENT_COUNT` | `k8s/churnsim.yaml` | 20 | number of `sim://` agents to distribute |
| `SIM_AGENT_MB` | `k8s/churnsim.yaml` | 0 | resident MB each *running* agent allocates (for GH-3959 overload runs) |
| `replicas` | `k8s/churnsim.yaml` | 3 | cluster size |
| memory `limits` | `k8s/churnsim.yaml` | 512Mi | the OOM kill-line for overload scenarios |
| `HealthCheckPollingTime` / `CheckAssignmentPeriod` | `Program.cs` | 2s / 5s | tightened from 10s/30s so a short rollout spans several control-plane cycles |

## Results

All runs on stock WolverineFx 5.39.0, minikube v1.39.0 (rootless podman,
containerd), 3 replicas, `maxSurge: 1` / `maxUnavailable: 0`,
`minReadySeconds: 15`.

### Run 1 — 20 agents, instant starts

- Steady state (2 min window): **0** `AssignmentChanged` rows — no churn at rest.
- One rolling deploy (~90 s, all 3 pods replaced):
  **22 `AssignmentChanged`, 22 `AgentStarted`, 5 `AgentStopped`** against a
  theoretical minimum of 20 (every agent must restart somewhere when all pods
  are replaced). Assignments settled 7/8/7 and matched what pods actually ran.
- Conclusion: with few agents that start instantly, 5.39's assignment plane is
  near-optimal. The GH-3987 pathology needs the ingredients the reporter had:
  **many agents** and **slow agent starts** (projections catching up), which
  stretch the overlap windows across many evaluation cycles.

### Run 2 — 200 agents, 250 ms start delay each

- One rolling deploy (~75 s): **433 `AssignmentChanged`** rows against a
  theoretical minimum of ~200 — **2.17× amplification** (vs 1.1× in run 1).
- `AssignmentChanged` (433) was more than double `AgentStarted` (201): the
  leader kept reassigning agents whose previous moves hadn't landed yet — the
  assignment ledger churning ahead of reality, which is the GH-3987 signature.
- Churn continued past the rollout: 50 / 359 / 24 rows across the three
  minutes spanning the deploy — the trailing 24 are post-deploy rebalance
  flapping after the topology had stopped changing.
- The cluster settled healthy (67/67/66 running, matching
  `wolverine_node_assignments`), so the "assigned but not running" wedge —
  a race, not a certainty — did not fire in this run.

### Run 3 — 500 agents, 500 ms start delay each

- One rolling deploy: **2,821 `AssignmentChanged`** rows (final, after
  convergence) against a minimum of ~500 — **5.6× amplification**. The curve
  across runs is super-linear: 1.1× → 2.17× → 5.6× as agent count and start
  latency grow. Extrapolated to the issue reporter's scale (hundreds of
  projection agents with multi-second catch-up starts), the reported 24k rows
  for one 3-pod rollout is consistent.
- **Degraded window**: 3 minutes after the rollout finished, only ~373 of 500
  agents were running, the distribution was badly skewed (167/168/40), and
  ~125 agents had *no assignment row at all*, with churn still flowing at
  ~500 rows/minute. Convergence to 167/168/167 took **4–5 minutes** after the
  deploy ended.
- **Duplicate agents (permanent until the next reshuffle)**: after
  convergence, cross-referencing per-pod `AGENT-START`/`AGENT-STOP` logs
  showed **6 agents concurrently running on two pods**
  (`sim://agent477..480`, `497`, `498`) — the stop for the old copy never
  landed, the assignment table claims a single owner, and nothing in 5.39
  ever notices. This is the "assignment table diverges from reality"
  failure GH-3987 describes (and the duplicate-agent shape of GH-2602);
  detection requires the node-side assigned-vs-running reconciliation the
  proposal adds.

### Summary

| run | agents | start delay | AssignmentChanged | amplification | settle after deploy | divergence |
|---|---|---|---|---|---|---|
| 1 | 20 | 0 | 22 | 1.1× | immediate | none |
| 2 | 200 | 250 ms | 433 | 2.17× | ~1 min | none observed |
| 3 | 500 | 500 ms | 2,821 | 5.6× | 4–5 min | 6 duplicated agents, minutes of missing/unassigned agents |

## Phase 2 (planned)

Rebuild the image against a locally patched Wolverine implementing the
proposal (stability-gated rebalance + node-side assigned-vs-running reconcile
+ per-node capacity ceiling with shed), rerun the same scripts, compare the
tables. See `formal/rolling-deploy/README.md` in the Wolverine repo for the
verified design.
