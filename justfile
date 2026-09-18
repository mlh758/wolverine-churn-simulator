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

# The RavenDB arm. Needs `just monitor-deploy` afterwards to be measurable at all.
deploy-ravendb version="6.39.0":
    ./scripts/deploy.sh {{version}} --backend ravendb

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

# Assignment churn from the store's own record history, on either arm.
measure:
    ./scripts/measure.sh

# Wolverine's spans out of Jaeger: where the assignment-dispatch wall-clock goes.
traces minutes="30":
    {{safetylab}} traces --minutes {{minutes}}

# Dump every live pod's raw JSON log into a run directory.
capture dir:
    ./scripts/capture-logs.sh {{dir}}

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

# One arm of the synthetic-self-guard A/B. PostgreSQL only; `just synth-guard-prep` first.
synth-guard arm:
    ./scripts/synth-guard-run.sh {{arm}}

# Point the app at a non-superuser role, so the RLS fault injection actually applies to it.
synth-guard-prep:
    ./scripts/synth-guard-prep.sh

# --------------------------------------------------------------------- capturing

# Begin a SafetyLab capture into runs/<name>.
capture-start name:
    ./scripts/monitor.sh start {{name}}

# Timestamp a phase boundary in the active capture.
mark label:
    ./scripts/monitor.sh mark {{label}}

# End the active capture.
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
    source scripts/backend.sh && require_safetylab && db_drop
    {{kubectl}} rollout restart deployment/churnsim
    {{kubectl}} rollout status deployment/churnsim --timeout=300s
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
