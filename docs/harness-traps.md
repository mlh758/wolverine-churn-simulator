# How this rig has lied to us

Every entry here produced a confident wrong answer at least once. They are grouped by what makes
them dangerous, not by component.

The common shape: **the harness measures something adjacent to what it claims**, and the result
looks plausible. A crash is cheap. A green run over an empty result set costs hours.

## The expensive class: checks that pass against nothing

**The leader advisory lock id is not `9999999`.** It is
`schemaName.GetDeterministicHashCode()` — `832201495` for schema `wolverine`. The
`PostgresqlNodePersistence.LeaderLockId = 9999999` constant sits in the same class and is *not
used by the leadership path*. Watching 9999999 finds no rows, and no rows reads as no violation:
S1–S4 all reported PASS against a lock nobody held. `pg_locks.objid` is an unsigned `oid`, so a
schema whose hash is negative appears as the 2³² complement — compare on the low 32 bits.

**Agent URIs carry a trailing slash.** `Uri.ToString()` normalises an authority-only URI, so the
leader row is `wolverine://leader/` and agents are `sim://agent1/`. Matching the unslashed literal
made every leader-row check silently find nothing.

Both were caught only by the deliberate sentinel in S1 — *"was the thing we are watching ever
observed at all?"* **Any new checker needs one.** A green run over an empty set is the default
failure mode of this entire approach, not an edge case.

## The one that took the machine down

**The minikube node advertises HOST cpu and memory.** Under rootless podman,
`.status.allocatable` reports 31 GB / 32 CPU regardless of `minikube start --memory=6g --cpus=4`.
Anything that sizes itself from "available memory" or CPU count sizes for the whole machine.

ClickHouse did exactly that twice: first aborting in `BackgroundSchedulePool` after sizing thread
pools for 32 cores, then — with `max_server_memory_usage_to_ram_ratio 0.3` — sizing for ~9 GB
against a 2 Gi container limit. Together with an OTel Collector reading every historical pod log
directory and retrying into a backend that never came up, it OOM-killed the minikube container out
of existence and took the host with it. Fedora did not recover; the machine needed a reboot.

**Rule: no memory-sizing infrastructure in this cluster.** Pod `limits` do constrain well-behaved
apps — churnsim's 512Mi limit works, and the GH-3959 overload runs depend on it — but they do not
save you from an app that reads host capacity and pre-allocates. Analysis belongs on the host:
structured JSON logs plus DuckDB (`scripts/logq.sh`) gives full SQL with nothing running in the
cluster at all. "The node reports 31 GB free" is not headroom; it is the absence of enforcement.

**RavenDB is in that same class, and the RavenDB arm runs it in-cluster anyway.** Its banner prints
`Phys Mem 30.386 GBytes` inside the container, and it sizes itself accordingly: measured on this
machine, an idle server with an *empty* database sat at **1.5 GB** resident. The store under test
cannot be moved to the host the way the analysis was, so the mitigation is to tell it the number
rather than let it read one — `RAVEN_Memory_MaxWorkingSet=1024` in `k8s/ravendb.yaml` brings the
same idle server to **131 MB**. The pod `limits` are the kill line, not the constraint; that env
var is the constraint. Do not remove it, and do not raise the limits in its place.

## Kubernetes selectors

**Terminating pods report `status.phase=Running`** for their whole
`terminationGracePeriodSeconds`. So `--field-selector=status.phase=Running` straight after a
rollout returns pods from the *previous* ReplicaSet, with the previous iteration's environment and
the previous iteration's agents. This made correctly-configured arms read as mislabelled and get
skipped — eight of sixteen iterations thrown away. Use `scripts/live_pods.py`: phase Running, no
`deletionTimestamp`, and Ready.

**But do not filter terminating pods out of the capture.** A terminating pod still holds a node
row, can still hold the lock, and is still running agents — a duplicate against its replacement is
*real*. `monitor.sh`'s follower deliberately uses the unfiltered pod list and keeps streaming until
each pod is actually gone. The selector decides *when the harness takes a measurement*, never
*what the measurement can see*. Duration, via the grace windows, is what separates handover
overlap from pathology.

**`kubectl logs` cannot reach a deleted pod.** During a rolling deploy the pods that matter most
are exactly the ones that disappear. Start the capture *before* the disturbance. A pod replaced
while nothing was following it contributes no residencies at all, which silently weakens S5/S6/S7
— hence C0's per-pod coverage check against `wolverine_nodes.description`.

**`kubectl logs -f` fails against a pod still in ContainerCreating**, and a single attempt marks it
as followed forever. That produced a false S4 ("leader lock held by an address that never announced
itself") on a healthy cluster. Retry until the pod is genuinely gone.

**`kubectl set env` is silently undone by `kubectl apply`'s three-way merge.** Env vars added with
`set env` are not in `last-applied-configuration`, so a later `deploy.sh` resurrects removed ones
and drops added ones. This mislabelled a whole experiment arm — a run reported as "stock default"
actually had the settle gate on. **Always verify configuration from the pod's own output**, and
poll the deployment spec until it matches intent before bouncing pods.

## Process lifecycle

**`monitor.sh stop` must kill process *groups*, not pids.** `start` detaches with `setsid`, so each
recorded pid is a group leader with children (`kubectl logs | safetylab harvest`). Killing the pid
leaves the children, and a leaked follower keeps appending the *next* experiment's pods into a
finished run's directory. One capture silently grew for twenty minutes after "stopped" and picked
up a different arm's pods. `stop` now kills groups and then warns if anything survived.

**A `while :` background loop makes `start` hang under any caller that waits for children**
(`bash -c`, a script, CI). Hence `setsid` + `disown` and a re-entry subcommand.

**A build cache keyed on existence never rebuilds.** `.tools/safetylab` was reused after source
edits, so a re-check silently ran the *old* checker against the *new* fix and reported the bug as
still present. Compare mtimes.

## Build and toolchain

**Host `obj/` leaks into the container image.** Both Dockerfiles restore from the `.csproj` alone
for layer caching, then `COPY` the project directory over it with `--no-restore`. If the host has
ever built locally, that second COPY drops a host `project.assets.json` full of
`/home/<user>/.nuget` paths onto the container's restore, and publish fails with `NETSDK1064:
package ... was not found` — which reads as a missing package and is really a leaked assets file.
`.dockerignore` excludes `**/bin` and `**/obj`. This became reachable the moment the repo grew a
local dev shell with an SDK in it.

**Nix flakes only see git-tracked files.** An untracked `flake.nix` fails with "not tracked by
Git". Using `.git/info/exclude` to keep the flake local defeats `nix develop` entirely — the flake
has to be committed on a checked-out branch.

## Shell

**`local a="$1" b="x${a}"` dies under `set -u`.** Bash expands every word of a `local` statement
before performing any of its assignments, so `b` reads an unset `a`. Split the statement.

**Inline `python3 -c '...'` inside a bash function is a quoting minefield.** An f-string with
nested double quotes died *silently into an empty field*, which shifted every later column in the
results TSV — the first row read plausibly and was wrong. Parsers live in files
(`scripts/verdict_row.py`, `scripts/live_pods.py`) where they can be tested standalone.

**Inline commands here run under zsh, the scripts under bash.** zsh globs `{.items[0]...}` in
kubectl jsonpath. Wrap ad-hoc kubectl in `bash -c`.

## Postgres and the app

**`wolverine_nodes.description` is the pod name.** Useful as a cross-check for pod↔node mapping,
and it is what C0's coverage check uses to notice pods the capture never attached to.

**The FK from `wolverine_node_assignments.node_id` is `on delete cascade`**, so ejecting a node
takes its assignment rows with it. An "assigned but not running" wedge can therefore never be a
dangling node reference.

**`wolverine_node_assignments.id` is the primary key**, so per-agent assignment uniqueness is
DB-enforced. S2/S3's table-side uniqueness checks are sentinels, not discoveries — the real
exclusivity question is answered from the pod log stream.

## RavenDB and the app

**The three replicas race to create the database, and the loser's failure is not one exception.**
RavenDB has no `CREATE DATABASE IF NOT EXISTS` and the client creates nothing implicitly, so
ChurnSim's first pod up has to — and all three start together and all three find no record.
Handling only `ConcurrencyException` ("it already exists") is not enough: a loser whose `PUT
/admin/databases` lands while the winner's brand-new database is inside
`UnloadAndLockDatabaseImpl` gets a 503 `DatabaseDisabledException` instead, *"unloaded and locked
because Checking if we need to recreate indexes"*. That propagates out of `UseWolverine` and kills
the host — one pod of three died this way on every cold start, which in Kubernetes is
CrashLoopBackOff on a store that is perfectly healthy. `RavenDbBackend.ensureDatabaseReady` is
therefore written around a postcondition (the record exists *and* the database answers
`GetStatisticsOperation`) and retries every failure identically, because every one of them has the
same correct response. Three consecutive cold starts after the fix: 3/3 pods up, 0 crashes, with
the retry visibly firing on one or two pods each time.

**`WolverineNode.Description` is `Environment.MachineName` on both backends** — the pod name in
Kubernetes, and the host name outside it. That is what makes C0's per-pod coverage check work
identically for RavenDB, and it is also why running the sim on a bare host makes C0 report every
node as uncaptured: all three processes share one machine name. Not a bug in the check.

**A RavenDB collection query is not an indexed query, and the difference is the whole trust
model.** `from AgentAssignments` with no `where` or `order by` is served straight from the
collection (`IndexName` comes back as `collection/AgentAssignments`) and cannot be stale; add a
filter and RavenDB builds an auto-index that can be. The monitor and `safetylab query` both stay on
the unfiltered form and filter client-side for exactly this reason, and both record `IsStale` and
the page limit anyway — a query that silently returns a *prefix* of the cluster would read as a
healthy, fully-placed cluster. C0 turns either into a failing coverage check rather than a note.

**A fresh RavenDB server is `passive` until something bootstraps it**, and every database operation
against it returns 503 `NodeIsPassiveException`. Creating a database is what bootstraps it, so the
monitor started against a cold cluster records a hole or two before the first ChurnSim pod is up.
That is the sampler working — it records the gap instead of dying — but do not read those first
samples as an outage.

## Measurement hygiene

**Sample volume scales with the assignment table.** At 500 agents every sample is ~65 KB; a 200 ms
tick writes ~200 MB for a ten-minute run with no added resolution over a control plane that ticks
at 2–5 s and grace windows of 15 s. 1 s is the right default here.

**Reset the schema between different Wolverine builds** (`reset-schema.sh`), and check that leftover
`SIM_*` env vars from a previous experiment are not still set — see the `set env` trap above.
`AgentStartBatchSize=5` survived three sessions this way and materially changed convergence.
