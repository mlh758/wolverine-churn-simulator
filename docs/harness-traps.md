# How this rig lies to you

Ways this harness has produced a confident wrong answer. The common shape: **it measures something
adjacent to what it claims**, and the result looks plausible. A crash is cheap; a green run over an
empty result set costs hours.

Everything above the last section is **still live** — a property of Kubernetes, podman, a store, or
the shell that will bite again. The last section lists incidents that now have a guard in code:
they are kept short, and only so nobody removes a guard without knowing what it is for.

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
thrown away. Live means phase Running, no `deletionTimestamp`, and Ready.

**But do not filter terminating pods out of the capture.** A terminating pod still holds a node
row, can still hold the lock, and is still running agents — a duplicate against its replacement is
*real*. `monitor.sh`'s follower deliberately uses the unfiltered list and streams until each pod is
genuinely gone. The selector decides *when a measurement is taken*, never *what it can see*;
duration, via the grace windows, is what separates handover overlap from pathology.

**`kubectl logs` cannot reach a deleted pod.** During a rolling deploy the pods that matter most
are exactly the ones that disappear, so start the capture *before* the disturbance. A pod replaced
while nothing followed it contributes no residencies, silently weakening S5/S6/S7 — hence C0's
per-pod coverage check.

**`kubectl logs -f` fails against a pod still in ContainerCreating**, and a single attempt marks it
followed forever. That produced a false S4 on a healthy cluster. Retry until the pod is gone.

**`kubectl set env` is silently undone by `kubectl apply`'s three-way merge.** Env vars added with
`set env` are not in `last-applied-configuration`, so a later `deploy.sh` resurrects removed ones
and drops added ones. This mislabelled a whole experiment arm — a run reported as "stock default"
actually had the settle gate on. **Verify configuration from the pod's own output**, and poll the
deployment spec until it matches intent before bouncing pods.

**`kubectl apply` reports "unchanged" when only an image's *contents* moved.** Every local image
here is tagged `:local`, so a rebuilt binary does not change the spec and the old pod keeps
running — every measurement afterwards silently comes from the previous build.

## Fault injection

**`kubectl delete pod --force --grace-period=0` is NOT an ungraceful kill.** It removes the API
object immediately, which makes it look like one, but the kubelet still delivers SIGTERM and a .NET
host still runs its shutdown. On the RavenDB arm a leader "killed" this way released its
compare-exchange lock and a peer took over in 6 s — a real number, and a measurement of *clean
handover* from a script built to measure what happens when shutdown never runs.

**`kubectl exec -- kill -9 1` does not work either.** The kernel does not deliver unhandled signals
to PID 1 from inside its own PID namespace, so it is silently a no-op. The kill must come from
outside the namespace: `minikube ssh` + `crictl inspect` for the host pid, then `sudo kill -9`.

**Before handing any pid to `kill -9`, check it is `> 1` and that `/proc/<pid>/cmdline` is the
process you meant.** A fault injector aimed at the wrong process either destroys the rig or, worse,
quietly injects nothing and leaves a plausible number behind.

## Shell

**`local a="$1" b="x${a}"` dies under `set -u`.** Bash expands every word of a `local` statement
before performing any of its assignments, so `b` reads an unset `a`. Split the statement.

**Inline `python3 -c '...'` inside a bash function is a quoting minefield.** An f-string with
nested double quotes died *silently into an empty field*, shifting every later column in a results
TSV — the first row read plausibly and was wrong. Parsers live in files where they can be tested
standalone (`scripts/live_pods.py`, `scripts/orphans.py`).

**A `while :` background loop makes its launcher hang** under any caller that waits for children
(`bash -c`, a script, CI). Hence `setsid` + `disown` and a re-entry subcommand in `monitor.sh`.

**Ad-hoc commands run under zsh; the scripts run under bash.** zsh globs `{.items[0]...}` in
kubectl jsonpath. Wrap one-off kubectl in `bash -c`.

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

## Measurement hygiene

**Sample volume scales with the assignment table.** At 500 agents a sample is ~65 KB; a 200 ms tick
writes ~200 MB for a ten-minute run with no added resolution over a control plane that ticks at
2–5 s against 15 s grace windows. 1 s is the right default.

**Reset the store between different Wolverine builds** (`reset-schema.sh`), and check that leftover
`SIM_*` env vars from a previous experiment are not still set. `AgentStartBatchSize=5` survived
three sessions this way and materially changed convergence.

**Never pool arms.** A rate averaged across two backends, two Wolverine versions or two knob
settings is not a result. Results files carry the backend per row and the scripts refuse to append
across arms.

## Guarded: incidents that can no longer recur

Each of these produced a wrong answer once and now has a guard. Listed so the guard is not removed
as dead weight — if you find yourself deleting one, this is what it was for.

| incident | guard |
|---|---|
| Watched advisory lock `9999999`; it is `schemaName.GetDeterministicHashCode()`. S1–S4 passed against a lock nobody held | `RunHistory.LockIdForSchema`, plus S1's sentinel note |
| Matched `wolverine://leader` without the trailing slash `Uri.ToString()` adds; every leader check found nothing | `RunHistory.IsLeaderUri` matches both forms |
| Argument parser ignored unrecognised flags, so a typo ran the verb against its default | `System.CommandLine` rejects unknown tokens; contract asserted in `CommandLineTests` |
| `cut -f2` on the word `none` read it as a node id and reported a 5-minute outage as instant recovery | `LeaderState` makes leaderless a type, not a string |
| `grep -m1 '"pid"'` on `crictl inspect` matched a namespace descriptor and ran `kill -9 1` on the node's init | `ContainerProbe` parses `.info.pid` and validates before any kill |
| `-o jsonpath='{.items[0]...}'` picked terminated and completed pods | `PodSelection.IsLive`; `safetylab pick-pod` |
| A "SIGKILL" run that actually shut down gracefully was reported as a failover measurement | `leader-kill.sh` counts `NodeStopped` and prints `*** INVALID RUN ***` |
| `.tools/safetylab` cached on existence, so a re-check ran the old checker against the new fix | mtime comparison in `monitor.sh`'s `ensure_tool` |
| Rebuilt monitor image ignored because the tag was unchanged | unconditional `rollout restart` in `monitor.sh deploy` |
| Host `obj/` leaked into the image, failing publish with `NETSDK1064` that read as a missing package | `.dockerignore` excludes `**/bin` and `**/obj` |
| `monitor.sh stop` killed pids not process groups; a leaked follower appended the next run's pods for twenty minutes | group kill, then a warning if anything survived |
| Two of three pods died on every RavenDB cold start racing to create the database | `RavenDbBackend.ensureDatabaseReady` retries on a postcondition |
| Duplicate-rate results from two backends would have pooled into one rate | backend column per row; the script refuses to append across arms |
