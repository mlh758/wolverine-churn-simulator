#!/usr/bin/env bash
# Pull every churnsim container's log of the tailer's CURRENT run -- live, replaced by a rollout,
# or killed -- off the node into a directory, one file per container attempt.
#
# DEPENDS ON  the node log tailer (k8s/logtail.yaml, applied by deploy.sh; `just logtail-deploy`
#             on an older cluster), $SAFETYLAB.
# REQUIRES    a live tailer pod that is following every live churnsim pod; both are refusals
#             (exit 2). A capture that silently lacked a live pod would read as complete.
# PRODUCES    <dir>/raw.<pod>.<attempt>.jsonl, the lines `kubectl logs` would print, decoded from
#             the node's CRI framing by `safetylab podlogs pull`. Queryable with scripts/logq.sh.
#
# THE ATTEMPT IS THE CONTAINER'S RESTART ORDINAL. A pod SIGKILLed by leader-kill or follower-kill
# comes back in the same pod as attempt 1; its victim's whole log is attempt 0. A pod a rollout
# replaced is there under its own name, marked `gone`. This is the whole reason the tailer exists:
# `kubectl logs` cannot reach either, and the kubelet deletes a replaced pod's file within about
# a minute of the pod.
#
# The tailer writes one directory per run (`safetylab podlogs start <name>`; capture-start and the
# kill scripts do it), holding every container alive at the run's start, whole, and everything
# after. `safetylab podlogs pull <dir> --run <name>` reaches an earlier run; `just logtail-reset`
# clears them all.
#
# ARGUMENTS
#
#   ./scripts/capture-logs.sh <dir>
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"

source scripts/backend.sh
require_safetylab

DIR="${1:?usage: capture-logs.sh <dir>}"

exec "$SAFETYLAB" podlogs pull "$DIR"
