# The entry point for every operation in this rig.
#
#   just              list the recipes
#   just <recipe>     run one
#
# THIS FILE IS A DISPATCH TABLE. No recipe may branch on a command's output, capture a value out of
# one, or parse anything. That rule is the whole reason it can exist: decisions in this rig live in
# src/SafetyLab/Cluster/ where tests/SafetyLab.Tests can exercise them without a cluster, and every
# time a decision has been taken by a text tool in a shell instead, it has eventually produced a
# confident wrong answer (docs/harness-traps.md).
#
# So a recipe here is allowed to: name a command, pass arguments to it, and run commands in order.
# If you find yourself wanting an `if` or a `$(...)`, that logic belongs in a `safetylab` verb.

set shell := ["bash", "-uc"]

kubectl := "minikube kubectl -- --context=minikube"
safetylab := ".tools/safetylab/safetylab"

# Show the recipes.
default:
    @just --list --unsorted

# ---------------------------------------------------------------------- building

# Build the host-side safetylab binary into .tools/.
build:
    dotnet publish src/SafetyLab -c Release -o .tools/safetylab -v q --nologo

# Unit tests plus the checkers' mutant ledger. Run this before trusting a measurement.
test:
    ./tests/selftest.sh

# --------------------------------------------------------------------- deploying

# Build the app image against a Wolverine version and deploy the store plus 3 replicas.
deploy version="6.39.0":
    ./scripts/deploy.sh {{version}}

# The MySQL arm. Measurable with no monitor (the store image ships a client); a SafetyLab
# CAPTURE still needs `just monitor-deploy`.
deploy-mysql version="6.39.0":
    ./scripts/deploy.sh {{version}} --backend mysql

# The RavenDB arm. Needs `just monitor-deploy` afterwards to be measurable at all.
deploy-ravendb version="6.39.0":
    ./scripts/deploy.sh {{version}} --backend ravendb

# E7's store: a 3-member RavenDB cluster at replication factor 3, one pinned churnsim pod per
# member, and the per-member monitor. Deploys the monitor itself, because forming the cluster
# runs through it. Needs `just ravendb-license` first.
deploy-ravendb-cluster version="6.39.0":
    ./scripts/deploy.sh {{version}} --backend ravendb --topology cluster

# Store a RavenDB license JSON (the free Developer license allows three nodes) as the Secret the
# replicated store reads. An unlicensed RavenDB refuses to add a second member.
ravendb-license file:
    {{kubectl}} create secret generic ravendb-license --from-file=license.json={{file}} --dry-run=client -o yaml | {{kubectl}} apply -f -

# Roll the pods with no code change (new pod template hash), the way a CD pipeline would.
rollout:
    ./scripts/rollout.sh

# Build and deploy the in-cluster monitor. Required before any RavenDB measurement.
monitor-deploy:
    ./scripts/monitor.sh deploy

# ------------------------------------------------------------------- measurement

# Running-vs-assigned, taken and judged in one process. Exit 0 clean, 1 diverged, 2 could not measure.
snapshot dir:
    {{safetylab}} snapshot {{dir}}

# Classify every window where one agent ran on two pods. Exit 0 never, 1 overlap, 2 could not analyse.
overlaps dir:
    {{safetylab}} overlaps {{dir}}

# Assignment churn from the store's own record history, on any arm.
measure:
    ./scripts/measure.sh

# Wolverine's spans out of Jaeger: where the assignment-dispatch wall-clock goes.
traces minutes="30":
    {{safetylab}} traces --minutes {{minutes}}

# Every churnsim container's log of the tailer's current run -- live, replaced or killed -- into a
# directory, raw.<pod>.<attempt>.jsonl each. Exit 2 if a live pod is not being followed.
capture dir:
    ./scripts/capture-logs.sh {{dir}}

# Which run the node log tailer is on, and is it following every live churnsim pod? Exit 1 if
# not, 2 if there is no tailer.
logtail-status:
    {{safetylab}} podlogs status

# Begin a tailer run by hand (capture-start and the kill scripts do it themselves): a fresh
# directory on the node, every live pod re-read from the head.
logtail-start name:
    {{safetylab}} podlogs start {{name}}

# Deploy the node log tailer (deploy.sh does this; here for a cluster deployed before it existed).
logtail-deploy:
    ./scripts/logtail.sh deploy

# Clear the tailer's node copy between experiments. Everything it held is GONE -- capture first.
logtail-reset:
    ./scripts/logtail.sh reset

# SQL over captured pod logs, on the host. `just logq runs/foo "select ..."`.
logq dir *sql="":
    ./scripts/logq.sh {{dir}} "{{sql}}"

# ------------------------------------------------------------------- experiments

# E2 — how often does a rolling deploy leave an agent running on two nodes?
duplicate-rate iterations="12":
    ./scripts/duplicate-rate.sh {{iterations}}

# E5 — does a duplicate heal, or does the cluster stay wrong?
heal-test iterations="8" watch="300":
    ./scripts/heal-test.sh {{iterations}} {{watch}}

# How long is the cluster leaderless after an UNGRACEFUL leader death?
leader-kill seconds="420":
    ./scripts/leader-kill.sh {{seconds}}

# The graceful control arm for the above — a clean handover, for comparison.
leader-kill-graceful seconds="120":
    MODE=graceful ./scripts/leader-kill.sh {{seconds}}

# Resolve the whole kill target and stop before the signal. Shows what would be destroyed.
leader-kill-dry-run:
    DRY_RUN=1 ./scripts/leader-kill.sh

# Kill a NON-leader ungracefully, so the leader survives and must rebalance. The nemesis that
# reproduces the missing-agent shortfall on both backends; leader-kill on RavenDB cannot reach it.
follower-kill seconds="420":
    ./scripts/follower-kill.sh {{seconds}}

# Resolve the spared leader, the victim and a validated host pid, and stop before the signal.
follower-kill-dry-run:
    DRY_RUN=1 ./scripts/follower-kill.sh

# E8 — take the leader's advisory lock away by killing the backend holding it. PostgreSQL only
# (MySQL's named lock is the same shape and KILL the same fault, but nothing injects it yet).
lock-kill seconds="180":
    ./scripts/lock-kill.sh {{seconds}}

# The control arm for the above: pg_cancel_backend interrupts the connection and the lock survives.
lock-kill-cancel seconds="180":
    MODE=cancel ./scripts/lock-kill.sh {{seconds}}

# Resolve the leader and the backend holding its lock, and stop before signalling.
lock-kill-dry-run:
    DRY_RUN=1 ./scripts/lock-kill.sh

# Who holds the leadership advisory lock right now? Exit 1 means nobody does.
lock-status:
    {{safetylab}} lock-chaos status

# E9 — cut the leader off from PostgreSQL and watch its lock strand. PostgreSQL only.
db-partition hold="180" settle="120":
    ./scripts/db-partition.sh {{hold}} {{settle}}

# E9's bounce arm — restart a peer mid-cut so the roster moves under the isolated leader, then
# measure the HEAL: does it converge, does it duplicate, for how long.
db-partition-bounce hold="240" settle="300":
    BOUNCE=1 ./scripts/db-partition.sh {{hold}} {{settle}}

# Resolve the leader, the store pod and the cut, and stop before arming anything.
db-partition-dry-run:
    DRY_RUN=1 ./scripts/db-partition.sh

# E7 — split brain: isolate the leader's store member (and the leader) from the rest, hold past
# the lock expiry, heal, and check. Needs `just deploy-ravendb-cluster`.
split-brain hold="420" settle="300":
    ./scripts/split-brain.sh {{hold}} {{settle}}

# Is a network partition still armed on the node from an earlier run? Exit 1 means yes.
partition-status:
    {{safetylab}} partition status

# Remove any armed partition. Idempotent, and safe to run when nothing is armed.
partition-heal:
    {{safetylab}} partition heal

# Each RavenDB member's view of the cluster, and the database's group topology.
raven-cluster-status:
    source scripts/backend.sh && require_safetylab && _raven raven-cluster status

# One arm of the synthetic-self-guard A/B. PostgreSQL only; `just synth-guard-prep` first.
synth-guard arm:
    ./scripts/synth-guard-run.sh {{arm}}

# Point the app at a non-superuser role, so the RLS fault injection actually applies to it.
synth-guard-prep:
    ./scripts/synth-guard-prep.sh

# --------------------------------------------------------------------- capturing

# Begin a SafetyLab capture into runs/<name>. Also starts a tailer run of that name.
capture-start name:
    ./scripts/monitor.sh start {{name}}

# Timestamp a phase boundary in the active capture.
mark label:
    ./scripts/monitor.sh mark {{label}}

# End the active capture: stop sampling, pull every pod's log off the node tailer, harvest it.
capture-stop:
    ./scripts/monitor.sh stop

# Run every checker over a captured run directory.
check name="":
    ./scripts/monitor.sh check {{name}}

# ----------------------------------------------------------------------- cluster

# On RavenDB the delete is a background operation and `safetylab admin reset-metrics` WAITS for it.
# Returning early would let the next measurement window open while the previous one is still being
# cleared, which silently pools two windows into one.
#
# Clear the node-record history so the next window starts at zero; the cluster keeps running.
reset-metrics:
    source scripts/backend.sh && require_safetylab && db_reset_metrics
    @echo "node records cleared"

# Bouncing is not optional on RavenDB: ChurnSim recreates the database on startup, and without a
# restart nothing recreates it and every node sits in a restart loop.
#
# Drop the store entirely and bounce the pods. Use between DIFFERENT Wolverine builds.
reset-schema:
    source scripts/backend.sh && require_safetylab && db_drop && bounce_workload
    @echo "store dropped and pods bounced"

# Wait for full placement to hold still. Target comes from the deployment's SIM_AGENT_COUNT.
settle:
    {{safetylab}} settle

# Is a fault still armed on this cluster from an earlier run? Exit 1 means yes.
chaos-status:
    {{safetylab}} chaos status

# Remove any injected fault. Idempotent, and safe to run when nothing is armed.
chaos-disarm:
    {{safetylab}} chaos disarm

# Assert every live pod reports this configuration from its own startup output.
# `just verify-config StaleNodeTimeout=4`
verify-config expect:
    {{safetylab}} verify-config --label app=churnsim --expect {{expect}}

# Live pods for a label — Running AND Ready AND not terminating.
pods label="app=churnsim":
    {{safetylab}} pods --label {{label}}

# What the cluster looks like right now.
status:
    {{kubectl}} get pods -o wide
