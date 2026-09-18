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

- **`src/ChurnSim`** — a .NET 10 console host running Wolverine against one of
  two message stores (see [Backends](#backends) below). It registers a custom
  `IStaticAgentFamily` (`sim://agent1..N`) whose `EvaluateAssignmentsAsync`
  calls `AssignmentGrid.DistributeEvenly` — the same distribution the Marten
  projection/subscription agents use, so the assignment plane behaves exactly
  like a production critter-stack app with N projections. Each agent logs
  `AGENT-START` / `AGENT-STOP`; an optional `SIM_AGENT_MB` knob gives each
  running agent a real memory footprint for overload (GH-3959) scenarios.
- **`k8s/`** — a single-pod store (PostgreSQL or RavenDB) and a 3-replica
  `churnsim` Deployment with a production-shaped rolling update (`maxSurge: 1`,
  `maxUnavailable: 0`, `minReadySeconds: 15`) so old pods drain while new
  pods have already joined the Wolverine cluster.
- **`scripts/`** — build/deploy, trigger a rolling deploy, and measure churn
  from the store's node-record history plus pod logs.

## Backends

The same simulation runs against two stores, because Wolverine's leader election
is not the same algorithm on both and the difference is the interesting part.

| | PostgreSQL | RavenDB |
|---|---|---|
| Leadership lock | session-scoped advisory lock, id `schemaName.GetDeterministicHashCode()` | compare-exchange document `wolverine/leader/<service>` with a **5-minute expiry** |
| Released by | the holding backend's death, server-side, immediately | **nothing** — a peer must notice the expiry and CAS-replace it |
| Lock names its owner | no; a `client_addr`, resolved through the pod identity map | yes, a node id in the document |
| Assignments | one row per agent, PK-enforced | one `AgentAssignments` document per agent, id-enforced |
| Reading them | `SELECT` | a query, which carries `IsStale` and a page limit |
| Store clock | `now()` | the HTTP `Date` header, second resolution |

The consequence worth stating plainly: **on RavenDB a leader that dies ungracefully
leaves the lock behind for up to five minutes, and no actor in the server clears
it.** The failure mode is not two leaders — it is *no* leader, for as long as
nobody looks, and it is invisible from inside every node because
`HasLeadershipLock()` reads a local field and never asks the server who owns the
key. That is what SafetyLab's **S8** exists to measure, and it has no PostgreSQL
counterpart at all.

The backend is a **build-time** choice: `ChurnSim.csproj` selects both the message
store package and the wiring file (`PostgresBackend.cs` / `RavenDbBackend.cs`) from
`-p:SimBackend=`, so an image is single-backend by construction. The deployment
declares the same value in `SIM_BACKEND` and ChurnSim **refuses to start on a
mismatch**, so a stale image cannot file results under the wrong store.

```bash
./scripts/deploy.sh                              # postgres (default), WolverineFx 5.39.0
./scripts/deploy.sh 6.39.0 --backend ravendb     # the RavenDB arm
```

Everything downstream is arm-agnostic: `scripts/backend.sh` reads `SIM_BACKEND` off
the deployment and routes each measurement to `psql` or to `safetylab query`, so
`measure.sh`, `reset-metrics.sh`, `reset-schema.sh`, `duplicate-rate.sh` and
`heal-test.sh` all work unchanged on either.

Two constraints specific to the RavenDB arm:

- **It needs a Wolverine build from after 2026-07-02**, when the native
  `ravendb://` control queue landed. Balanced durability needs a control endpoint
  and there is no fallback — `UseTcpForControlEndpoint()` advertises
  `tcp://localhost`, which no peer pod can reach. `deploy.sh` rejects 5.x outright
  and ChurnSim checks again at startup.
- **Measurement goes through the safetylab pod** (`./scripts/monitor.sh deploy`),
  because there is no `psql` to `kubectl exec` into. The monitor already speaks
  RavenDB's REST API and takes no Wolverine dependency, so it doubles as the query
  tool rather than needing a second image.

`scripts/synth-guard-*.sh` are PostgreSQL-only and say so: they inject faults with
row-level security, which RavenDB has no equivalent of. They refuse to run on the
RavenDB arm rather than silently measuring something else.

## Environment setup (one time)

Podman must be installed (the native CLI, not the Desktop flatpak). Then:

```bash
# minikube as a plain user-space binary
curl -sLo ~/.local/bin/minikube \
  https://storage.googleapis.com/minikube/releases/latest/minikube-linux-amd64
chmod +x ~/.local/bin/minikube

# rootless podman driver
minikube config set rootless true
minikube config set memory 8192
minikube config set cpus no-limit
minikube start --driver=podman --container-runtime=containerd

# kubectl comes along for free
minikube kubectl -- get nodes
```

`memory` is a real ceiling — the rootless podman driver passes it through and the kernel enforces
it on the minikube container's cgroup, which is what keeps a runaway pod off the host. `cpus` is
**not** passed through by that driver, so it is set to `no-limit` rather than to a number that
would quietly do nothing; every result in `RESULTS.md` was taken on an unconstrained CPU shape.
Note the ceiling does not propagate *inwards*: `/proc/meminfo` still reports the host's memory to
everything in the cluster, so anything that sizes itself from available memory needs an explicit
budget — see [docs/harness-traps.md](docs/harness-traps.md).

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
./scripts/deploy.sh          # build image, load into minikube, deploy the store + 3 replicas
./scripts/reset-metrics.sh   # zero the node-record history once the cluster is settled
./scripts/rollout.sh         # one rolling deploy: 3 pods replaced one at a time
./scripts/measure.sh         # churn report
```

Add `--backend ravendb` to `deploy.sh` (plus `./scripts/monitor.sh deploy`, which the
RavenDB arm needs before it can be measured) and the other three are unchanged.

`measure.sh` reports:

- node-record counts by event type — `AssignmentChanged` is the
  churn signal the issue reporter counted (24k rows for one 3-pod rollout in
  their production system);
- `AssignmentChanged` rows per minute, to see the churn concentrated in the
  rollout window;
- current node registrations and per-node agent assignment counts — after the
  cluster settles, every `sim://` agent should have exactly one assignment on
  a live node. Discrepancies between the assignment set and what
  pods actually run are the "assigned but not running" symptom;
- `AGENT-START` / `AGENT-STOP` counts from live pod logs (note: logs of
  replaced pods are gone, so the durable node-record numbers are the source
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
| S1 | At most one holder of the leader lock | Postgres: sentinel — if it trips, the lock id is wrong and every leader check below is vacuous. **RavenDB: a real check** — leadership held under both `wolverine/leader` and `wolverine/leader/<service>` |
| S2 | At most one `wolverine://leader` assignment row | sentinel — already enforced, by a PK on Postgres and by document identity on RavenDB |
| S3 | The lock and the leader assignment row agree | sustained split between "who holds the lock" and "who owns the row" |
| S4 | The lock is not held by a departed node | the stacking / failover-stall fingerprint (on RavenDB, expected for up to the lock's 5-minute expiry — read with S8) |
| S8 | The lock is not left expired but unclaimed | **RavenDB only** — an expired compare-exchange lock nobody has taken over: no leader until a peer looks. SKIPs on Postgres, where a lock has no expiry |
| S5 | **No agent runs on two nodes at once** | the user-visible property: a doubled projection daemon or exclusive listener |
| S6 | Every assigned agent is running on its assigned node | GH-3987's "assigned but not running" wedge |
| S7 | Every running agent is assigned to the node running it | an orphan runner — the precursor to S5 |
| L1 | The cluster converges after the last phase marker | one leader, every agent placed once, and it stays that way (on RavenDB a *present but expired* lock is not a leader) |
| C0 | Observation coverage | sampling gaps, failed samples, monitor↔store clock offset, and — on RavenDB — a query that came back stale or short of its page limit |

S3–S8 are grace-windowed (`--grace`, default 15s): handover is not atomic, so a tick or two
of disagreement is the protocol working. What gets reported is divergence that does not end.

The checkers themselves are backend-agnostic: every leader-side check reads
`RunHistory.LeaderHolders`, which normalises "a granted advisory lock, attributed through the
identity map" and "a compare-exchange document that names its owner" into the same shape. Which
evidence a sample carries is recorded in its meta record, and a run directory captured before the
RavenDB arm existed has no `backend` field — those load and check as PostgreSQL, unchanged.

### Running it

```bash
nix develop                              # dotnet 10 + kubectl + podman + jq
./scripts/monitor.sh deploy              # build + deploy the in-cluster monitor (once;
                                         # picks the manifest matching the deployed arm)

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
mutant ledger: each synthetic fixture injects one fault and asserts which checks must go red —
and that the others stay green. The `LEDGER` array in the script is the authoritative copy.

| fixture | fault injected | must go red |
|---|---|---|
| `clean` | none — a healthy PostgreSQL run | *nothing* |
| `dup-agent` | an agent started on a second pod, assignment table untouched | S5, S7 |
| `orphan-lock` | departed node's advisory lock never released | S3, S4, L1 |
| `stranded` | assignment row for an agent the pod never started | S6 |
| `no-converge` | two agents never placed after the rollout | L1 |
| `split-leader` | leader row and advisory lock name different nodes | S3 |
| `gap` | a hole in the sample stream | C0 |
| `raven-clean` | none — a healthy RavenDB run, so the leader checks cannot pass vacuously on an empty `locks` array | *nothing* |
| `raven-expired-lock` | compare-exchange lock expired and nobody took it over | S8, L1 |
| `raven-split-key` | leadership held under both spellings of the key at once | S1, L1 |
| `raven-truncated` | a store read that came back short of its page limit | C0 |

Run it with `nix develop --command ./tests/selftest.sh`. Add a checker, add a fixture and a
ledger row.

The same command also runs **`tests/SafetyLab.Tests`**, which covers the two layers outside the
checkers:

- **cluster decisions** — which pods are live, resolving a container to a host pid, reading leader
  state, and whether a fault injector actually fired. These run against captured fixtures (real
  `kubectl get pods -o json`, real `crictl inspect`), so they need no cluster.
- **the CLI contract** — every invocation the scripts and `k8s/` manifests issue must parse, and
  malformed ones must be rejected. Parsing only; nothing is executed.

Both are pure functions and finish in well under a second, so there is no reason not to run them
before touching a live experiment.

## Leader failover (`scripts/leader-kill.sh`)

Kills the pod currently holding leadership and times how long the cluster has no leader, writing a
per-poll timeline to `runs/leader-kill-<backend>/timeline.tsv` (leader pod and node, lock holder,
seconds to lock expiry, agents placed).

```bash
nix develop                                    # needs the host-side safetylab binary
DRY_RUN=1 ./scripts/leader-kill.sh             # resolve the target, signal nothing
./scripts/leader-kill.sh 420                   # SIGKILL, watch for 420s
MODE=graceful ./scripts/leader-kill.sh 120     # control arm: an ordinary pod delete
```

The default mode SIGKILLs the container's process from the node, outside its PID namespace, so no
shutdown hook runs. That distinction is the whole experiment: a graceful shutdown releases the
leadership lock and hands over in seconds, while an ungraceful one leaves the lock behind to be
recovered by whatever the backend's own mechanism is. `MODE=graceful` measures the first for
comparison.

The script asserts its own nemesis fired. `NodeStopped` records are written only on the graceful
shutdown path, so one appearing during a SIGKILL run means the victim shut down cleanly and the
run is reported `*** INVALID ***` rather than as a failover measurement.

## Structured logs and SQL

ChurnSim writes JSON logs when `SIM_JSON_LOGS=true` — each line a JSON object whose `State` holds
the message-template parameters as named fields, so `AgentUri` and `NodeNumber` arrive queryable
instead of embedded in prose. Capture and query them on the host:

```bash
./scripts/capture-logs.sh runs/foo     # raw.<pod>.jsonl per pod, no transformation
./scripts/logq.sh runs/foo             # schema + summary
./scripts/logq.sh runs/foo "select json_extract_string(state,'\$.AgentUri') as agent,
                                   count(distinct pod) as pods
                              from logs where message like 'AGENT-START%'
                             group by 1 having count(distinct pod) > 1;"
```

DuckDB reads the files directly, so this is full SQL — window functions, self-joins — with no
database to run. An in-cluster ClickHouse was tried and abandoned: it sized itself from the node's
advertised *host* RAM, which rootless podman does not constrain, and took the machine down. See
[docs/harness-traps.md](docs/harness-traps.md).

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
| `SIM_BACKEND` | `k8s/churnsim*.yaml` | `postgres` | which store this arm is; must match the image's build-time `-p:SimBackend=` or ChurnSim refuses to start |
| `RAVENDB_URLS` / `RAVENDB_DATABASE` | `k8s/churnsim-ravendb.yaml` | `http://ravendb:8080` / `churnsim` | RavenDB arm only; ChurnSim creates the database if it is missing |
| `RAVEN_Memory_MaxWorkingSet` | `k8s/ravendb.yaml` | 1024 (MB) | **do not remove** — RavenDB otherwise sizes itself from the host's 30 GB, see [harness-traps.md](docs/harness-traps.md) |
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

For the RavenDB arm, swap `Wolverine.Postgresql` (and `Wolverine.RDBMS`, which it does not
need) for `src/Persistence/Wolverine.RavenDb/Wolverine.RavenDb.csproj` in that list. The two
arms' package sets are disjoint apart from core, which is the point of the build-time switch:
neither has to be packed to run the other.

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

> **ChurnSim targets net10.0, and that is the baseline.** net8 and net9 are both near end of
> support and net11 is already out, so the rig stays pinned to one current runtime rather than
> tracking a retired one. The overload scenario was rerun on net10.0 on 2026-09-11 and reproduces
> the net9.0 shape; that entry in RESULTS.md replaced the older numbers outright. Entries dated
> before 2026-09-11 were taken on net9.0 unless they say otherwise; compare within a framework,
> not across.
