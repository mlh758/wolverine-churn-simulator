#!/usr/bin/env bash
# SQL over captured pod logs, on the host.
#
# DEPENDS ON  duckdb (from `nix develop`), and a directory of raw.*.jsonl from capture-logs.sh or
#             `safetylab snapshot`.
# REQUIRES    at least one raw.*.jsonl in <dir>; exits 2 otherwise.
# PRODUCES    query results on stdout. Reads only.
#
# The view `logs` exposes: pod, ts, level, category, message, state (JSON), file.
#
# Deliberately on the host and not in the cluster: an in-cluster ClickHouse sized itself from the
# node's advertised host RAM (rootless podman does not enforce `minikube --memory`) and took the
# machine down. See docs/harness-traps.md.
#
# ARGUMENTS
#
#   ./scripts/logq.sh <dir>              schema + a summary
#   ./scripts/logq.sh <dir> "select ..." arbitrary SQL over the logs view
set -uo pipefail
cd "$(dirname "$0")/.."

DIR="${1:?usage: logq.sh <run-dir> [sql]}"
shift || true
SQL="${*:-}"

command -v duckdb >/dev/null || { echo "no duckdb -- run inside 'nix develop'" >&2; exit 2; }

shopt -s nullglob
files=("$DIR"/raw.*.jsonl)
[ ${#files[@]} -gt 0 ] || { echo "no raw.*.jsonl in $DIR -- run capture-logs.sh first" >&2; exit 2; }

# regexp_extract pulls the pod name back out of the filename; union_by_name tolerates the schema
# drift between Wolverine's own records and ChurnSim's.
VIEW="create or replace view logs as
      select regexp_extract(filename, 'raw\\.(.*)\\.jsonl', 1) as pod,
             try_cast(Timestamp as timestamp)                  as ts,
             LogLevel                                          as level,
             Category                                          as category,
             Message                                           as message,
             State                                             as state,
             filename                                          as file
        from read_json_auto('$DIR/raw.*.jsonl', filename=true, union_by_name=true,
                            format='newline_delimited', ignore_errors=true);"

if [ -z "$SQL" ]; then
    SQL="select pod, count(*) as lines, min(ts) as first, max(ts) as last
           from logs group by pod order by pod;
         select category, count(*) as n from logs group by category order by n desc limit 10;"
fi

duckdb -c "$VIEW $SQL"
