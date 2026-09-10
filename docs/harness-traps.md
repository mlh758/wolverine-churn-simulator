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

## Measurement hygiene

**Sample volume scales with the assignment table.** At 500 agents every sample is ~65 KB; a 200 ms
tick writes ~200 MB for a ten-minute run with no added resolution over a control plane that ticks
at 2–5 s and grace windows of 15 s. 1 s is the right default here.

**Reset the schema between different Wolverine builds** (`reset-schema.sh`), and check that leftover
`SIM_*` env vars from a previous experiment are not still set — see the `set env` trap above.
`AgentStartBatchSize=5` survived three sessions this way and materially changed convergence.
