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

**Inline `python3 -c '...'` inside a bash function is a quoting minefield.** An f-string with
nested double quotes died *silently into an empty field*, shifting every later column in a results
TSV — the first row read plausibly and was wrong. Parsers live in files where they can be tested
standalone. The measurement path has since gone further and left both shell and Python entirely:
`safetylab snapshot` takes both sides of the running-vs-assigned comparison and `safetylab
overlaps` replays the duplicate-healing timeline, each deciding its verdict in the same process
that computes its counts, and `safetylab traces` does the same for Jaeger's spans. There is no
text hop left to mis-read and no inline `python3 -c` left anywhere. The only Python remaining is
two quoted `<<'PY'` heredocs, in `duplicate-rate.sh` and `heal-test.sh`, that total up a finished
results.tsv — off the measurement path, and quoted, which is the half of this trap that bites.

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

**An experiment can depend on instrumentation that only exists in a locally packed build.** The
synthetic-self-guard runs count a log line that the branch builds emit and no released Wolverine
does: `strings` on a deployed 6.39.0 `WolverineFx.dll` finds the marker zero times. Everything
upstream of the count still works — the fault arms, a real node really is blinded, the phases run,
the snapshots come back clean — so the run looks entirely healthy right up to a summary of zeroes.
Before trusting an arm, check the marker exists in the build under test; `safetylab count` over the
`during` phase is the cheapest way, and the run now prints `*** INVALID ARM ***` when it is zero.

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
| `.tools/safetylab` cached on existence, so a re-check ran the old checker against the new fix. It recurred while the snapshot/overlaps/traces verbs were being written: `dotnet build` succeeded, `.tools/` still held the previous publish, and the new verb came back "Unrecognized command" | mtime comparison in `monitor.sh`'s `ensure_tool` **and** in `backend.sh`'s `require_safetylab`, which every measurement script calls |
| Rebuilt monitor image ignored because the tag was unchanged | unconditional `rollout restart` in `monitor.sh deploy` |
| Host `obj/` leaked into the image, failing publish with `NETSDK1064` that read as a missing package | `.dockerignore` excludes `**/bin` and `**/obj` |
| `monitor.sh stop` killed pids not process groups; a leaked follower appended the next run's pods for twenty minutes | group kill, then a warning if anything survived |
| Two of three pods died on every RavenDB cold start racing to create the database | `RavenDbBackend.ensureDatabaseReady` retries on a postcondition |
| Duplicate-rate results from two backends would have pooled into one rate | backend column per row; the script refuses to append across arms |
| `sed` read three counters out of a Python differ's summary line; when that line was a traceback all three came back empty, `${x:-0}` made them zero, the iteration was filed `clean` and its raw logs deleted | `safetylab snapshot` computes the counts and the verdict together and exits 0/1/**2**; `duplicate-rate.sh` branches on the code and parses nothing |
| "I could not measure" and "I measured, and it was clean" were the same outcome | `SKIP-unmeasurable`, from snapshot's exit 2 — refused on no live pod, an unreadable store, or no agent event logged anywhere |
| The overlap analysis wrote its errors to the same stdout its counts were read from, so a traceback read as `never` — "no duplicate at all" — and heal-test.sh then deleted the raw logs | `safetylab overlaps` exits 0/1/**2**; heal-test.sh branches on the code, and exit 2 is `SKIP-unanalysable` with the evidence kept |
| The RLS fault was armed and disarmed by two statements a few hundred seconds apart, with no trap. A Ctrl-C or any failing command between them left one node's row permanently invisible to the app role, and every later experiment on that cluster silently measured a crippled node | `safetylab chaos arm/disarm/status`; `disarm` is idempotent and runs from a `trap … EXIT INT TERM`, and a run refuses to start while a previous arm is still in place |
| The victim query returned an empty string on an unsettled cluster, so the policy became `id <> ''`, which hides nothing. The arm ran its full duration injecting no fault and recorded zero injections — indistinguishable from "the guard prevented it" | `PostgresChaos.TryChooseVictim` refuses with a named reason for every empty case; the summary prints `*** INVALID ARM ***` when the armed phase logged no injections |
| Pod configuration was checked with `grep … \|\| echo WARNING` and the run carried on | `safetylab verify-config` refuses, checks **every** live pod, and reads `State.Setting`/`State.Value` so "knob absent from this build" is distinct from "set to something else" |
| `monitor.sh start` followed `.items[0]` for the safetylab pod, straight after a `rollout restart` left the previous one terminating — a near-empty history.jsonl that every S-check passes over | `safetylab pick-pod --label app=safetylab` |
| One `kubectl logs -f` for the monitor stream: a restarted pod or an API blip left the rest of the run with no server-side history, while `stop` still reported a plausible sample count | a re-attaching `__history` loop, and `stop` warns loudly when history.jsonl is empty |
| `active_run`'s `exit 2` never stopped anything — it is only ever called inside `$(…)`, so the refusal printed and the caller continued with an empty name. `mark` appended a phase boundary to `runs/marks.jsonl`, outside any run | it `return`s, and every caller is `name=$(active_run) \|\| exit 2` |
| `.active` outlived its capture and its run directory, so `mark` and `check` pointed at a path that no longer existed | both refuse when `runs/<name>` is missing, and say how to clear the marker |
| Three copies of `wait_settled` compared the placed count against a literal **500**, against a deployment that declares `SIM_AGENT_COUNT` and a ChurnSim whose default is 20. Any other number could never settle, so every iteration would record SKIP-no-settle — quietly, for as long as it was left running | `safetylab settle` reads the target from the deployment; `--expect` overrides it |
| `db_placed` sent psql's stderr to `/dev/null`, so an unreachable store returned `""`, `${n:-0}` made it 0 placed, and the loop waited out its full timeout before reporting "never settled" — a store outage filed as a convergence failure | `settle` exits **2** after five consecutive failed reads, and `_psql` no longer discards stderr |
| `duplicate-rate.sh` checked the settle status on the pre-rollout wait and not on the measured one, so an iteration that never converged still had its snapshot taken and filed as a real result | the measured settle is checked; no-settle is `SKIP-no-settle`, an unreadable store is `SKIP-unmeasurable` |
| Three of five `backend.sh` store queries — `db_per_node`, `db_records`, `db_per_minute` — had `group by 1` over a select list whose first expression contains an aggregate, or `order by 2` over a one-column list. All three errored on **every call since they were written**; `2>/dev/null` hid it, so `measure.sh` printed empty sections and `total AssignmentChanged: 0` | the queries are fixed, and `_psql` no longer discards stderr — which is the only reason this was ever found |
| A crashed checker produced no output, so `actual` came back empty — which is exactly what the `clean` rows expect. The two fixtures whose job is to prove the checkers stay quiet passed against a broken binary | `tests/selftest.sh` asserts a non-empty result array, and that every fixture ran the *same* number of checks |
