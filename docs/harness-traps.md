# How this rig lies to you

Ways this harness has produced a confident wrong answer. The common shape: **it measures something
adjacent to what it claims**, and the result looks plausible. A crash is cheap; a green run over an
empty result set costs hours.

Every entry here is **still live** — a property of Kubernetes, podman, a store, or the shell that
will bite again. When a trap gets a guard in code, its entry is deleted.

## Four rules, each bought the hard way

1. **Every check needs a sentinel: "was the thing I am watching ever observed at all?"** Not "did
   it violate", but "did it exist". A checker that silently matches nothing reports PASS, which is
   the default failure mode of this whole approach rather than an edge case.
2. **Configuration must fail loudly when it is not understood.** Silent acceptance of a flag,
   value or env var means measuring something nobody asked for, with nothing in the output saying
   so.
3. **Shell invokes processes; decisions are typed and tested.** Reading structured data (JSON, TSV)
   with text tools is how this rig has most often gone wrong. Decision logic lives in
   `src/SafetyLab/Cluster/` with tests in `tests/SafetyLab.Tests` so it runs without a cluster.
4. **A fault injector must prove it fired.** "No violation observed" after a nemesis that silently
   did nothing is worse than no run at all, because it looks like evidence.

## minikube under rootless podman

**The node advertises HOST cpu and memory.** `.status.allocatable` reports 31 GB / 32 CPU
regardless of what `minikube start` was told. Anything sizing itself from "available memory" or CPU
count sizes for the whole machine.

**The two halves of that are not equally toothless** (measured 2026-09-18, Fedora 44, cgroup v2,
`cpu io memory pids` delegated to the user slice):

| flag | reaches the container? | enforced? |
|---|---|---|
| `--memory` | yes (`HostConfig.Memory`) | **yes** — it is `memory.max` on the minikube cgroup |
| `--cpus` | **no** (`NanoCpus = 0`, `CpuQuota = 0`) | no — `cpu.max` is `max`, `nproc` is 32 |

So there is a real kernel ceiling on total cluster memory, and it is what stands between a runaway
pod and the host. What it does **not** do is tell anything inside about itself: `/proc/meminfo` is
not namespaced, so every process still reads the host's `MemTotal` and sizes for it. The limit
converts a host crash into an in-cluster OOM kill — much better, still a ruined run.

CPU is simply unconstrained. `podman update --cpus=N minikube` would change that, but every number
in RESULTS.md was taken on the unconstrained shape, and this rig measures timing and cadence — so
that is not a free knob.

**Rule: no memory-sizing infrastructure in this cluster.** An in-cluster ClickHouse once sized
thread pools for 32 cores and its heap for ~9 GB against a 2 Gi limit, OOM-killed the minikube
container out of existence and took the host with it; the machine needed a reboot. Pod `limits`
constrain well-behaved apps (churnsim's 512Mi limit works, and the GH-3959 overload runs depend on
it) but do not save you from one that reads host capacity and pre-allocates. Analysis belongs on
the host — JSON logs plus DuckDB (`scripts/logq.sh`) is full SQL with nothing running in-cluster.

**RavenDB is in that class and the RavenDB arm runs it in-cluster anyway.** Its banner prints
`Phys Mem 30.386 GBytes` inside the container and it sizes accordingly: an idle server with an
*empty* database sat at **1.5 GB** resident. It cannot be moved to the host the way the analysis
was, so it is told the number instead — `RAVEN_Memory_MaxWorkingSet=1024` in `k8s/ravendb.yaml`
brings that same idle server to **131 MB**. The pod `limits` are the kill line; that env var is the
constraint. Do not remove it and do not raise the limits in its place.

## Kubernetes behaviour

**Terminating pods report `status.phase=Running`** for their whole
`terminationGracePeriodSeconds`. So `--field-selector=status.phase=Running` straight after a
rollout returns pods from the *previous* ReplicaSet, with the previous iteration's environment and
agents. This made correctly-configured arms read as mislabelled — eight of sixteen iterations
thrown away. Live means phase Running, no `deletionTimestamp`, and Ready. But that selector
decides *when a measurement is taken*, never *what it can see*: a terminating pod still holds a
node row, can still hold the lock, and is still running agents, and a duplicate against its
replacement is *real*. The pod side of a capture comes off the node log tailer, which follows the
node's files and not a pod list, so nothing filters it; duration, via the grace windows, is what
separates handover overlap from pathology.

**`kubectl set env` is silently undone by `kubectl apply`'s three-way merge.** Env vars added with
`set env` are not in `last-applied-configuration`, so a later `deploy.sh` resurrects removed ones
and drops added ones. This mislabelled a whole experiment arm — a run reported as "stock default"
actually had the settle gate on. **Verify configuration from the pod's own output**, and poll the
deployment spec until it matches intent before bouncing pods.

**`kubectl apply` reports "unchanged" when only an image's *contents* moved.** Every local image
here is tagged `:local`, so a rebuilt binary does not change the spec and the old pod keeps
running — every measurement afterwards silently comes from the previous build.

**`kubectl logs --tail=N` on a stream of 65 KB JSON lines returns fragments.** The container runtime
splits long lines into partial log entries and `--tail` counts those, so the last "line" is the
tail of a sample and the one before it is the head of another; a JSON parser sees "extra data".
Read the whole stream (as `monitor.sh` does) or tail generously and keep only lines that parse.

**`kubectl rollout status` refuses a StatefulSet whose update strategy is `OnDelete`** — "rollout
status is only available for RollingUpdate strategy type" — and under `set -e` that ends the
deploy with the store half up. Both StatefulSets on the replicated arm use `OnDelete` on purpose
(an automatic rolling update would replace a pod, and its IP, and so its side of the cut, under a
running partition), so they are waited on with `wait_ready` in backend.sh: pods exist, then
`kubectl wait --for=condition=Ready`. Staged, because `kubectl wait` errors when no pod matches yet.

**A pod's IP is the partition.** The cut is keyed on pod IPs, and a pod replaced during the hold
comes back on a new IP outside every rule. `OnDelete` keeps the controller from doing that; nothing
keeps a crash from doing it. The pinned minority pod can still reach its own member throughout, so
it has no reason to crash — but if a run shows a churnsim pod restart inside the partition window,
that pod escaped the cut for the rest of the hold, and the run is not what it claims.

## Fault injection

**`kubectl delete pod --force --grace-period=0` is NOT an ungraceful kill.** It removes the API
object immediately, which makes it look like one, but the kubelet still delivers SIGTERM and a .NET
host still runs its shutdown. On the RavenDB arm a leader "killed" this way released its
compare-exchange lock and a peer took over in 6 s — a real number, and a measurement of *clean
handover* from a script built to measure what happens when shutdown never runs.

**`kubectl exec -- kill -9 1` does not work either.** The kernel does not deliver unhandled signals
to PID 1 from inside its own PID namespace, so it is silently a no-op. The kill must come from
outside the namespace: `minikube ssh` + `crictl inspect` for the host pid, then `sudo kill -9`.

**An advisory lock cannot be released from another session, so an injector that tries injects
nothing.** `pg_advisory_unlock(id)` and `pg_advisory_unlock_all()` only ever touch the **calling**
session's own lock table. Run from psql against the lock a Wolverine leader holds, the lock stays
exactly where it was: the function returns `f` and emits `WARNING: you don't own a lock of type
ExclusiveLock` — on **stderr**, which `2>/dev/null` puts out of sight, leaving a run that injected
no fault and reports success. The only way to take a session-level advisory lock away from outside
is to end the session: `pg_terminate_backend(pid)`. `pg_cancel_backend` is not it either: the
server keeps the session, and so the lock, and only the running statement dies (`ERROR: canceling
statement due to user request`) — which is why that is E8's control arm rather than a second
treatment. Both functions answer `false` rather than raising when the pid has already gone, and
**psql still exits 0 under `ON_ERROR_STOP=1`** while printing `WARNING: PID n is not a PostgreSQL
backend process` to stderr, so the exit code says nothing and the boolean has to be read. (All of
the above measured 2026-09-19 against PostgreSQL 17.7; the cluster runs 16-alpine and none of it
is version-dependent.)

**`boolean::text` is `true`, and psql renders the same column as `t`.** A parser that knows one
spelling silently drops every row the day somebody adds a cast — and for a lock-holder query an
empty result reads as "the cluster has no leader", which is a refusal rather than a crash and
therefore quiet. `LockChaos` spells the boolean `'t'`/`'f'` in the SQL itself and accepts both
forms anyway. `safetylab lock-chaos kill`
reads it, then reads the lock back from the server, and **K1** repeats the assertion against the
capture.

**`NodeStopped` is not a graceful-shutdown witness on every store.** `leader-kill.sh` and
`follower-kill.sh` print `*** INVALID RUN ***` when a `NodeStopped` record appears, which only works
if a graceful shutdown writes one. RavenDB does (73 records, 2026-09-18). The MySQL arm, on 6.39.0,
wrote **none** across some twenty graceful shutdowns in the retained window, so there `0 → 0` is what
a clean stop *and* a SIGKILL both report. Validate a kill from the container instead: exit code
**137**, `restartCount` up by one on the same pod, and the victim's log — `$OUT/post/raw.<pod>.0.jsonl`,
the lower attempt — ending with no shutdown lines.

**Before handing any pid to `kill -9`, check it is `> 1` and that `/proc/<pid>/cmdline` is the
process you meant.** A fault injector aimed at the wrong process either destroys the rig or, worse,
quietly injects nothing and leaves a plausible number behind.

**The minikube node's `iptables` cannot load its tables; `iptables-nft` can.** Under rootless
podman `iptables -S` fails with "can't initialize iptables table `filter': Table does not exist"
(`ip_tables` is not present and the node cannot insmod), while `iptables-nft` — the binary
kube-proxy's own rules are written with — works. There is no `tc`-based alternative worth the
trouble: pod-to-pod traffic on the one node is *forwarded* by the host kernel (kindnet's `ptp`
CNI, one veth pair and a /32 route per pod), so a `DROP` in `FORWARD` keyed on two pod IPs cuts
exactly one ordered pair. Insert at position 1, not append: `KUBE-FORWARD` accepts established
flows earlier in the chain, and a rule appended after it would cut new connections while every
existing one carried on through the "partition". Every rule this rig writes carries the comment
`safetylab-partition`, and the heal deletes only lines carrying it — kube-proxy's rules in the same
chain are not ours to touch.

## Shell

**`local a="$1" b="x${a}"` dies under `set -u`.** Bash expands every word of a `local` statement
before performing any of its assignments, so `b` reads an unset `a`. Split the statement.

**A `while :` background loop makes its launcher hang** under any caller that waits for children
(`bash -c`, a script, CI). Hence `setsid` + `disown` and a re-entry subcommand in `monitor.sh`.

**Ad-hoc commands run under zsh; the scripts run under bash.** zsh globs `{.items[0]...}` in
kubectl jsonpath. Wrap one-off kubectl in `bash -c`.

**A NuGet `Version="x"` is a MINIMUM, not a pin.** Asking for a version that does not exist is
warning NU1603, not an error, and NuGet resolves whatever else it can find — a build arg of
`0.0.0-nope` silently produced an image running **WolverineFx 0.8.2**. Every WolverineFx reference
is pinned `[$(WolverineVersion)]`, NuGet's exact-version notation, so a typo is NU1102 and the
build stops. Anything measuring one specific build needs the brackets.

**An MSBuild global property cannot be reassigned by the project it is passed to.** `-p:X=`
(empty) leaves X empty; it does not fall through to a `Condition="'$(X)' == ''"` default. The
Dockerfile therefore omits `-p:WolverineVersion` entirely when no build arg was given, rather than
passing it empty.

**Nix flakes only see git-tracked files.** An untracked `flake.nix` fails with "not tracked by
Git", and `.git/info/exclude` defeats `nix develop` entirely — the flake has to be committed.

## The stores

**`wolverine_nodes.description` / `WolverineNode.Description` is `Environment.MachineName`** — the
pod name in Kubernetes, the host name outside it. That is the pod↔node join on both backends, and
it is why running the sim on a bare host makes C0 report every node as uncaptured: the processes
share one machine name. Not a bug in the check.

**PostgreSQL: the FK from `wolverine_node_assignments.node_id` is `on delete cascade`**, so ejecting
a node takes its assignment rows with it — an "assigned but not running" wedge can never be a
dangling node reference. **`wolverine_node_assignments.id` is the primary key**, so per-agent
uniqueness is DB-enforced; S2/S3's table-side checks are sentinels, not discoveries.

**RavenDB: a collection query is not an indexed query, and the difference is the trust model.**
`from AgentAssignments` with no `where` or `order by` is served straight from the collection
(`IndexName` is `collection/AgentAssignments`) and cannot be stale; add a filter and RavenDB builds
an auto-index that can be. The monitor and `safetylab query` stay on the unfiltered form and filter
client-side for that reason, and record `IsStale` and the page limit regardless — a query returning
a *prefix* of the cluster would read as a smaller, healthy one.

**RavenDB: a fresh server is `passive` until something bootstraps it**, and every database
operation returns 503 `NodeIsPassiveException` until then. Creating a database is what bootstraps
it, so a monitor started against a cold cluster records a hole or two before the first ChurnSim pod
is up. That is the sampler working; do not read those samples as an outage.

**RavenDB: three passive servers are three singletons, not a cluster.** Creating a database on one
of them bootstraps *that one* into a one-member cluster and replicates to nobody. The replicated
arm forms the cluster explicitly (`safetylab raven-cluster form`: bootstrap, add members, create
the database at factor 3) BEFORE the first ChurnSim pod, because a ChurnSim pod that finds no
database creates one at factor 1 — and a factor-1 database on a three-member cluster would let
every partition finding be filed against a store that was never replicated. On that arm
`RAVENDB_REPLICATION_FACTOR=3` also covers a `reset-schema`.

**RavenDB: an unlicensed server refuses to add a cluster member.** `PUT /admin/cluster/node` on a
`license: None` server answers `402 LicenseLimitException`, after bootstrapping perfectly happily
(measured 2026-09-18, 7.0.9). The free Developer license allows three nodes and is a registration
against an email address, so nothing in this rig can obtain it; `deploy.sh --topology cluster`
refuses while the `ravendb-license` Secret is absent rather than forming one member and calling it
a cluster. The members read `RAVEN_License` at START, and the StatefulSet's `OnDelete` strategy
means `apply` never restarts a pod — so a license added after the pods came up sits unread until
they are deleted. The cluster deploy deletes them every time for that reason.

**RavenDB: the client fails over, and a partition experiment needs it not to.** The client fetches
the database group's topology and routes to any member it can reach. Cut a pod's member off and,
with topology updates on, the pod quietly reroutes to the majority: no minority side exists and the
run measures client failover instead of a split. `RAVENDB_PIN_NODE=true` sets
`DisableTopologyUpdates`; the replicated arm's pods are pinned by ordinal and the partition cuts
the pod as well as its member, so P1 can tell the difference.

**RavenDB: the headless Service is the wrong thing to read a replicated store through.** It
resolves to every member round-robin, so under a partition a monitor reading `ravendb:8080` would
see two realities alternating with nothing saying which is which. The replicated arm's monitor is
given every member url and reads them all on every tick; the first is the primary view and the
rest are compared against it.

## Measurement hygiene

**Sample volume scales with the assignment table.** At 500 agents a sample is ~65 KB; a 200 ms tick
writes ~200 MB for a ten-minute run with no added resolution over a control plane that ticks at
2–5 s against 15 s grace windows. 1 s is the right default.

**Reset the store between different Wolverine builds** (`just reset-schema`), and check that leftover
`SIM_*` env vars from a previous experiment are not still set. `AgentStartBatchSize=5` survived
three sessions this way and materially changed convergence.

**Never pool arms.** A rate averaged across two backends, two Wolverine versions or two knob
settings is not a result. Results files carry the backend per row and the scripts refuse to append
across arms.

**`deploy.sh` re-applies the manifest, which resets every `kubectl set env` override.** A scenario
set with `set env` *before* a deploy is gone afterwards, silently, and the pods come back on
`k8s/churnsim.yaml`'s defaults — 500 agents, no ballast. Those pods then repopulate the store, so
the next run measures a mix. Observed twice on 2026-09-23 (`max(agent id)` 493, then 342, against
`SIM_AGENT_COUNT=60`). Set the scenario **after** the deploy, and verify by reading `max(agent id)`
back out of the assignment table rather than trusting the deployment's env block. The node log
tailer's run name is set the same way and reset to `unscoped` by the same deploy; `safetylab
podlogs` reads the run back from the live pod for that reason, and `just logtail-status` says
which run it is on.

**`just reset-schema` drops the store with the workload still running.** `db_drop` then
`bounce_workload` means live pods recreate the schema and refill it during the drop, and a pod from
the outgoing generation writes its agents into the "clean" store. For anything where the agent set
itself changed, reset **cold**: `scale --replicas=0`, wait for deletion, drop, scale back.

**`observe.sh`'s `RUNNING` column is only usable early in a window.** It counts `AGENT-START` minus
`AGENT-STOP` over `kubectl logs --since=<window>`, so once a STOP's matching START has aged out of
the sliding window the difference goes wrong — `-1` and `0` both appear in the 2026-09-23 cascade
arms while the assignment rows were provably steady. Read `ROWS` for what is placed.

**A capacity-aware arm can advertise nothing and still look like an arm.** Post-GH-4589
`MemoryPressureLoadMonitor` returns null when it cannot find a memory limit to divide by, and the
leader reads a node advertising null as *having headroom*. So a run where the monitor found no
cgroup ceiling is stock placement behaviour wearing the arm's label, with no error anywhere.
ChurnSim prints a `LOAD-MONITOR` line per pod naming its denominator; `denominator=NONE` voids the
arm. The same line distinguishes the pre-GH-4589 GC-budget divisor from the cgroup one, which
matters because **readings on the two scales differ by 0.75 and no threshold carries across** (see
RESULTS.md 2026-09-23).

**The cascade experiment ran no checker at all, for its whole life.** `runs/*/observe.sh` is raw
`psql` plus `kubectl logs`; it never wrote a `history.jsonl`, so no SafetyLab check has ever been
evaluated against a GH-3959 run. Every cascade number in RESULTS.md before 2026-09-23 comes from
counting rows in a table, which is why a detached agent that landed nowhere read as "capacity-aware
holds". If an experiment is worth a result, capture it (`just capture-start`) and check it.
