# wolverine-deploy-sim

This is a project to test Wolverine in adverse environments. This has tools
to quickly roll deployments, overload pods, kill leader connections, etc.

The idea is to quickly reach weird failure states and see what we can do
to fix them.

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
- **`src/SafetyLab`** — the measurement and fault-injection tool, and where every
  decision this rig makes lives. It watches the cluster from outside, checks safety
  and liveness properties over a captured run, and carries the verbs the scripts
  call. Unit-tested against captured fixtures, so it runs without a cluster.
- **`scripts/` + `justfile`** — build/deploy, trigger a rolling deploy, run an
  experiment. `just` is the index; the scripts are still callable directly.

## Backends

The same simulation currently supports RavenDB and PostgreSQL. The PG backend
is built around acquiring an advisory lock with a long lived session. The other
RDBMS implementations like MySQL and Oracle use a similar mechanism by taking
a lock on a deterministic row. RavenDB uses a cluster-wide CAS operation to
place the node ID and claim leadership.

The backend is a **build-time** choice: `ChurnSim.csproj` selects both the message
store package and the wiring file (`PostgresBackend.cs` / `RavenDbBackend.cs`) from
`-p:SimBackend=`, so an image is single-backend by construction. The deployment
declares the same value in `SIM_BACKEND` and ChurnSim **refuses to start on a
mismatch**, so a stale image cannot file results under the wrong store.

```bash
just deploy             # postgres (default)
just deploy-ravendb     # the RavenDB arm
```

The Wolverine version under test lives in **[`wolverine-version`](wolverine-version)**,
read by `Directory.Build.props`, the Dockerfile and `deploy.sh` alike. Edit it to move the whole
rig to a new release; pass a version to `just deploy <v>` to override for one run. Every
`WolverineFx` reference is pinned to an *exact* version (`[6.39.0]`), so a version that does not
exist fails the restore instead of quietly resolving something older.

Everything downstream is arm-agnostic: the backend is read off the deployment and each
measurement routed to `psql` or to `safetylab query`, so every measurement recipe works unchanged
on either arm.

Two constraints specific to the RavenDB arm:

- **It needs a Wolverine build from after 2026-07-02**, when the native
  `ravendb://` control queue landed. Balanced durability needs a control endpoint
  and there is no fallback — `UseTcpForControlEndpoint()` advertises
  `tcp://localhost`, which no peer pod can reach. `deploy.sh` rejects 5.x outright
  and ChurnSim checks again at startup.
- **Measurement goes through the safetylab pod** (`just monitor-deploy`),
  because there is no `psql` to `kubectl exec` into. The monitor already speaks
  RavenDB's REST API and takes no Wolverine dependency, so it doubles as the query
  tool rather than needing a second image.

`scripts/synth-guard-*.sh` are PostgreSQL-only: they inject faults with
row-level security, which RavenDB has no equivalent of. They refuse to run on the
RavenDB arm rather than silently measuring something else.

### The replicated RavenDB arm

`just deploy-ravendb-cluster` swaps the single RavenDB pod for a **three-member cluster at
replication factor 3** (`k8s/ravendb-cluster.yaml`), useful for testing
network partition (`just split-brain`, [docs/experiments.md](docs/experiments.md)). Nothing measured
on it is comparable to the single-member RavenDB results, and the results file says which is which.

**It needs a RavenDB license.** An unlicensed server runs one node and refuses to add a second
(`402 LicenseLimitException`). The free Developer license allows three; request it at
<https://ravendb.net/license/request>, then `just ravendb-license path/to/license.json` stores it as
the Secret every member reads. The deploy refuses to start without it.

Three things differ from the single-member arm, and each is there because without it there is no
split to observe:

- **ChurnSim is a StatefulSet and each pod is pinned to one member** — `churnsim-N` talks to
  `ravendb-N` and, with `RAVENDB_PIN_NODE=true`, never learns the others exist. The RavenDB client
  otherwise fetches the group topology and fails over to any member it can reach, so cutting a pod's
  member off would make it quietly reroute to the majority. Every script reads the workload kind
  through `backend.sh`'s `sim_workload`.
- **The cluster is formed, and the database created at factor 3, before the first ChurnSim pod
  starts.** `deploy.sh` runs `safetylab raven-cluster form` through the monitor pod. A ChurnSim pod
  that finds no database creates one at factor 1, and three servers with a factor-1 database is not
  a replicated store.
- **The monitor reads every member, not the Service** (`k8s/safetylab-ravendb-cluster.yaml`). Under
  a partition the members disagree and that disagreement is the finding; read through the headless
  Service the history would flicker between two views. `ravendb-0` is the primary view that fills
  the checker-facing fields, and the partition script keeps it on the majority side.

The partition itself is `safetylab partition arm|heal|status`: `DROP` rules in the minikube node's
`FORWARD` chain, keyed on pod IPs, both directions of every cross pair, each carrying a comment so
`heal` removes exactly what `arm` added and nothing of kube-proxy's. The observer's pod is on
neither side. `arm` reads the rules back and refuses a partial arm; `heal` runs from a trap and
refuses to report success while any rule remains; a run refuses to start over a cut a previous run
left behind — the same three-verb shape as the PostgreSQL chaos injector, for the same reasons.

## Running things

```bash
just            # every recipe, with what it does
just deploy     # the usual starting point
```

`just --list` is the index — the [justfile](justfile) is the list, so this README does not keep a
second copy of it. Likewise `safetylab --help` for the measurement and fault-injection verbs.

Two conventions worth knowing before you read either:

**Every verb keeps "I could not measure" separate from "I measured, and it was fine"** — exit 2,
not exit 0. A store that cannot be reached, a pod whose log carries no agent events, a fault that
armed but never injected: each is its own outcome, because a green run over an empty result set is
this rig's most expensive failure mode.

**The justfile is a dispatch table and nothing else.** A recipe may name a command and pass it
arguments, and that is all — it never branches on output or captures a value. Every decision lives
in `src/SafetyLab/Cluster/`, where `tests/SafetyLab.Tests` exercises it without a cluster, because
every time a decision has been taken by a text tool in a shell it has eventually produced a
confident wrong answer. See [docs/harness-traps.md](docs/harness-traps.md).

The scripts under `scripts/` are still callable directly; `just` is just the front door.

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
the cluster. `safetylab` runs on the host — `nix develop` provides the .NET 10
SDK for it, along with `just`, kubectl, podman, duckdb and jq.

> ⚠️ **kubectl context**: every script pins `--context=minikube` explicitly.
> Do not run bare `kubectl apply` from this repo — if your default kubeconfig
> context points at a real cluster), that is
> where the manifests would land. Keep the pin if you edit the scripts.

## Running a baseline

```bash
just deploy          # build image, load into minikube, deploy the store + 3 replicas
just reset-metrics   # zero the node-record history once the cluster is settled
just rollout         # one rolling deploy: 3 pods replaced one at a time
just measure         # churn report
```

Use `just deploy-ravendb` (plus `just monitor-deploy`, which the RavenDB arm needs
before it can be measured) and the other three are unchanged.

`just measure` reports:

- node-record counts by event type — `AssignmentChanged` is the
  churn signal
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
| S9 | Every store member agrees on assignment ownership | **replicated RavenDB only** — a member holding an agent under a different owner than the primary view, or lacking its document, past the grace window. Assignments are single-member writes with async replication; this is two claims on one agent seen from outside |
| S10 | No store member reports document conflicts | **replicated RavenDB only** — RavenDB's own `CountOfConflicts` above zero on any member: two members accepted incompatible writes to one document. Zero grace |
| L1 | The cluster converges after the last phase marker | one leader, every agent placed once, and it stays that way (on RavenDB a *present but expired* lock is not a leader) |
| P1 | The partition took | **partition runs only** — between the `partition-start` and `partition-heal` marks at least one member must have reported losing its Raft leader. A run where none did was not partitioned, whatever the firewall said. SKIPs when the marks are absent |
| C0 | Observation coverage | sampling gaps, failed samples, monitor↔store clock offset, and — on RavenDB — a query that came back stale or short of its page limit; on the replicated arm, a member the monitor could not read |

S3–S9 are grace-windowed (`--grace`, default 15s): handover is not atomic, so a tick or two
of disagreement is the protocol working. What gets reported is divergence that does not end.

On the replicated arm S1 is a check **across members**: every member's compare-exchange value is
read every tick and tagged with its source, members that agree collapse into one holder, and
members that name different owners for one key are two. That is what a leadership split looks
like from outside a Raft cluster that cannot itself hand a key to two owners — the minority member
keeps serving the last value it committed.

The checkers themselves are backend-agnostic: every leader-side check reads
`RunHistory.LeaderHolders`, which normalises "a granted advisory lock, attributed through the
identity map" and "a compare-exchange document that names its owner" into the same shape. Which
evidence a sample carries is recorded in its meta record, and a run directory captured before the
RavenDB arm existed has no `backend` field — those load and check as PostgreSQL, unchanged.

### Running it

```bash
nix develop                        # dotnet 10 + kubectl + podman + duckdb + just
just monitor-deploy                # build + deploy the in-cluster monitor (once per arm)

just capture-start rollout-1       # begin capturing BEFORE the disturbance
just rollout
just mark rollout-end
sleep 120                          # let it settle, so L1 has a tail to judge
just capture-stop
just check rollout-1
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
and that the others stay green. It also refuses to pass if the checker produced no results at
all, so a crashed binary cannot satisfy the two fixtures whose expected answer is "nothing".

Run it with `just test`. The `LEDGER` array at the top of the script is the list of fixtures and
what each must trip; add a checker, add a fixture and a ledger row.

The same command also runs **`tests/SafetyLab.Tests`**, which covers the two layers outside the
checkers:

- **cluster decisions** — which pods are live, resolving a container to a host pid, reading leader
  state, and whether a fault injector actually fired. These run against captured fixtures (real
  `kubectl get pods -o json`, real `crictl inspect`), so they need no cluster.
- **the CLI contract** — every invocation the scripts and `k8s/` manifests issue must parse, and
  malformed ones must be rejected. Parsing only; nothing is executed.

Both are pure functions and finish in well under a second, so there is no reason not to run them
before touching a live experiment.

## Leader failover

Kills the pod currently holding leadership and times how long the cluster has no leader, writing a
per-poll timeline to `runs/leader-kill-<backend>/timeline.tsv` (leader pod and node, lock holder,
seconds to lock expiry, agents placed).

The default mode SIGKILLs the container's process **from the node, outside its PID namespace**, so
no shutdown hook runs. That distinction is the whole experiment: a graceful shutdown releases the
leadership lock and hands over in seconds, while an ungraceful one leaves the lock behind to be
recovered by whatever the backend's own mechanism is. There is a graceful arm to measure the first
for comparison, and a dry run that resolves the whole target and signals nothing — `just --list`
has all three.

The script asserts its own nemesis fired. `NodeStopped` records are written only on the graceful
shutdown path, so one appearing during a SIGKILL run means the victim shut down cleanly and the
run is reported `*** INVALID ***` rather than as a failover measurement.

## Split brain (E7)

`just split-brain [hold] [settle]` on the replicated RavenDB arm. It settles the cluster, finds the
leader, hands leadership off gracefully if it sits on `ravendb-0` (the monitor's primary view has to
stay on the majority side), then isolates **the leader's store member and the leader's pod
together** from the other two for `hold` seconds — default 420, past the 300 s compare-exchange
lock expiry — heals, watches for `settle` seconds, and runs everything: the per-member history
through the checkers, the post-heal pod logs through `safetylab overlaps`, and RavenDB's own
conflict state through `safetylab query conflicts`.

That is `CUT=zone`, the default: a zone-level partition that no client can route around, and it
needs the pods pinned (`RAVENDB_PIN_NODE=true`, the manifest's setting) so the stranded pod is the
leader by construction. `CUT=store` is the control: pods left alone and un-pinned
(`RAVENDB_PIN_NODE=false`), only the member the default client prefers cut from the other two —
asking what the default client does. The script verifies the pin from the pods' own output and
refuses a mismatch. Both have been run once; see RESULTS.md.

Read `P1` in the report before anything else: it says whether the cut actually took. Then `S1`
(two members naming two lock owners), `S9` (a member holding assignments the majority does not),
`S10` (the store's own conflict count), `S5` (an agent running on two pods), and the run
directory's `timeline.tsv` for the glance-at-it view, one column group per member. The reasoning
behind the design, and the prediction it tests, is E7 in [docs/experiments.md](docs/experiments.md).

## Structured logs and SQL

ChurnSim writes JSON logs when `SIM_JSON_LOGS=true` — each line a JSON object whose `State` holds
the message-template parameters as named fields, so `AgentUri` and `NodeNumber` arrive queryable
instead of embedded in prose. Capture and query them on the host:

```bash
just capture runs/foo     # raw.<pod>.jsonl per pod, no transformation
just logq runs/foo        # schema + summary
just logq runs/foo "select json_extract_string(state,'\$.AgentUri') as agent,
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
just traces 30                  # span durations + percentiles for the last 30 min
kubectl port-forward svc/jaeger 16686:16686   # then browse localhost:16686
```

`set env` is undone by the next `just deploy` — `kubectl apply`'s three-way merge drops it — so
re-apply it after a redeploy, or the traces silently stop arriving.

ChurnSim only wires up OpenTelemetry when `SIM_OTLP_ENDPOINT` is set, so a run without it stays
byte-for-byte comparable with earlier results.

## Knobs

| Knob | Where | Default | Meaning |
|---|---|---|---|
| `SIM_BACKEND` | `k8s/churnsim*.yaml` | `postgres` | which store this arm is; must match the image's build-time `-p:SimBackend=` or ChurnSim refuses to start |
| `RAVENDB_URLS` / `RAVENDB_DATABASE` | `k8s/churnsim-ravendb.yaml` | `http://ravendb:8080` / `churnsim` | RavenDB arm only; ChurnSim creates the database if it is missing. On the replicated arm the url is `ravendb-$(POD_INDEX)`, one member per pod |
| `RAVENDB_PIN_NODE` | `k8s/churnsim-ravendb-cluster.yaml` | `false` (`true` on the replicated arm) | `DisableTopologyUpdates` on the RavenDB client: the pod talks only to the url it was given and never fails over. Off, a partition is invisible — the client reroutes to the majority |
| `RAVENDB_REPLICATION_FACTOR` | `k8s/churnsim-ravendb-cluster.yaml` | 1 (3 on the replicated arm) | what ChurnSim creates the database with when it finds none; matters after a `reset-schema` on the replicated arm |
| `RAVEN_Memory_MaxWorkingSet` | `k8s/ravendb.yaml`, `k8s/ravendb-cluster.yaml` | 1024 / 768 (MB) | **do not remove** — RavenDB otherwise sizes itself from the host's 30 GB, see [harness-traps.md](docs/harness-traps.md) |
| `SIM_AGENT_COUNT` | `k8s/churnsim.yaml` | 500 | number of `sim://` agents to distribute (ChurnSim falls back to 20 if the var is absent, which `safetylab settle` would then wait for) |
| `SIM_AGENT_MB` | `k8s/churnsim.yaml` | 0 | resident MB each *running* agent allocates (for GH-3959 overload runs) |
| `replicas` | `k8s/churnsim.yaml` | 3 | cluster size |
| memory `limits` | `k8s/churnsim.yaml` | 512Mi | the OOM kill-line for overload scenarios |
| `HealthCheckPollingTime` / `CheckAssignmentPeriod` | `Program.cs` | 2s / 5s | tightened from 10s/30s so a short rollout spans several control-plane cycles |
| Wolverine version | `wolverine-version` | 6.39.0 | the build under test; `Directory.Build.props`, the Dockerfile and `deploy.sh` all read this one file |

## Where to read more

**[docs/experiments.md](docs/experiments.md)** — what we are hunting, the experiment catalogue,
what counts as a finding versus an artifact, and what is currently open.

**[docs/harness-traps.md](docs/harness-traps.md)** — the ways this rig has produced confident
wrong answers. Read it before trusting a number.

## Results

Measured runs live in **[RESULTS.md](RESULTS.md)**, dated newest first

**[ARCHIVED_RESULTS.md](ARCHIVED_RESULTS.md)** holds interesting findings that have since been patched.

Wolverine ships often. This rig measures what is shipping, so the default is the latest released
package and old arms are archived rather than carried.

## Bisecting a Wolverine branch

`localfeed/` is gitignored and **empty by default**. The directory
itself must exist (the Dockerfile COPYs it). Populate it only to bisect a branch or a pre-release;
otherwise pass a released version straight to `deploy.sh`. To pack a branch:

```bash
# stock baseline from pristine main
git -C ~/GitHub/wolverine worktree add ../wolverine-stock --detach origin/main
for p in src/Wolverine/Wolverine.csproj \
         src/Persistence/Wolverine.RDBMS/Wolverine.RDBMS.csproj \
         src/Persistence/Wolverine.Postgresql/Wolverine.Postgresql.csproj \
         src/Wolverine.RuntimeCompilation/Wolverine.RuntimeCompilation.csproj; do
  (cd ~/GitHub/wolverine-stock && dotnet pack $p -p:Version=6.40.0-local.1 -o ~/GitHub/wolverine-deploy-sim/localfeed)
done

just deploy 6.40.0-local.1
```

For the RavenDB arm, swap `Wolverine.Postgresql` (and `Wolverine.RDBMS`, which it does not
need) for `src/Persistence/Wolverine.RavenDb/Wolverine.RavenDb.csproj` in that list. The two
arms' package sets are disjoint apart from core, which is the point of the build-time switch:
neither has to be packed to run the other.

Use `just reset-schema` between different Wolverine builds — leftover rows from another build's
schema are not a baseline. [docs/experiments.md](docs/experiments.md) has the scenario recipes
(churn shape, the GH-3959 overload cascade, the synthetic-self guard) and what each one is
looking for.
