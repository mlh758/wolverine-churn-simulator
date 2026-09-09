#!/usr/bin/env python3
"""Print the names of pods that are genuinely live, given `kubectl get pods -o json` on stdin.

`--field-selector=status.phase=Running` is not enough, and the difference silently wrecked an
experiment. A pod that is shutting down keeps `phase: Running` for its whole
terminationGracePeriodSeconds, so straight after a rollout the "Running" set contains pods from
the *previous* ReplicaSet — with the previous iteration's environment and the previous
iteration's agents. Sampling those made a correctly-configured arm look mislabelled and would
have made a clean cluster look like it had orphaned agents.

Live means: phase Running, no deletionTimestamp, and Ready.
"""

import json
import sys


def is_live(pod: dict) -> bool:
    if pod.get("metadata", {}).get("deletionTimestamp"):
        return False
    status = pod.get("status", {})
    if status.get("phase") != "Running":
        return False
    return any(
        c.get("type") == "Ready" and c.get("status") == "True"
        for c in status.get("conditions", [])
    )


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except Exception as e:  # noqa: BLE001
        print(f"live_pods: could not parse kubectl output: {e}", file=sys.stderr)
        return 1

    for pod in payload.get("items", []):
        if is_live(pod):
            print(pod["metadata"]["name"])
    return 0


if __name__ == "__main__":
    sys.exit(main())
