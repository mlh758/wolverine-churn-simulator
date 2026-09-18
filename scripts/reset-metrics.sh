#!/usr/bin/env bash
# Clear the node-record history so the next measurement window starts at zero.
# (Leaves nodes/assignments untouched -- the cluster keeps running.)
#
# postgres: truncate wolverine.wolverine_node_records
# ravendb:  delete every NodeRecords document, and WAIT for the delete to finish -- RavenDB does
#           it as a background operation, so returning early would let the next measurement
#           window start while the previous one is still being cleared.
set -euo pipefail
cd "$(dirname "$0")/.."

source scripts/backend.sh

db_reset_metrics
echo "node records cleared ($(sim_backend))"
