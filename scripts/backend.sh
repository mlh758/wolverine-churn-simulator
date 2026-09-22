#!/usr/bin/env bash
# Sourced by the measurement scripts. Not executable on its own.
#
# DEPENDS ON  a deployed store. The RavenDB path additionally needs a live safetylab pod
#             (./scripts/monitor.sh deploy) and returns 2 with that instruction when there is none.
#             The PostgreSQL and MySQL paths exec a client that ships in the store's own image, so
#             they need nothing but the store.
# REQUIRES    nothing; every function reports rather than assumes.
# PRODUCES    tab-separated text on stdout. db_reset_metrics and db_drop MUTATE; the rest read.
#
# The query text is frozen. Every number in RESULTS.md was taken through these exact queries, and
# rewording one breaks comparability with the whole existing record for no gain.
#
# The exception, 2026-09-18: db_per_node, db_records and db_per_minute had `group by 1` over a
# select list whose first expression contains an aggregate, and `order by 2` over a one-column
# list. All three errored on every call since they were written, and psql's stderr went to
# /dev/null, so measure.sh printed empty sections and a confident `total ...: 0`. No number ever
# came through them, so there was no comparability to protect.
#
#   source scripts/backend.sh
#   sim_backend            -> postgres | mysql | ravendb
#   sim_topology           -> single | cluster   (cluster = the replicated RavenDB store, E7)
#   sim_workload           -> deployment/churnsim | statefulset/churnsim
#   bounce_workload        -> restart every churnsim pod and wait, on either workload kind
#   require_safetylab      -> build $SAFETYLAB if stale, or exit 2
#   db_placed              -> count of placed sim:// agents
#   db_nodes               -> node number <TAB> description (= pod name)
#   db_per_node            -> node id <TAB> agent count
#   db_records             -> record type <TAB> count, descending
#   db_per_minute [event]  -> minute <TAB> count (default AssignmentChanged)
#   db_reset_metrics       -> clear the node-record history, leaving the cluster running
#   db_drop                -> drop the whole store, for switching Wolverine builds

export PATH="$HOME/.local/bin:$PATH"
: "${K:=minikube kubectl -- --context=minikube}"

# The host-side binary, which carries most of what these scripts used to decide.
: "${SAFETYLAB:=.tools/safetylab/safetylab}"

# Rebuilds when any source is newer than the binary. An existence check alone is not enough: a
# stale binary does not fail, it answers with yesterday's code.
require_safetylab() {
    command -v dotnet >/dev/null || {
        echo "no dotnet on PATH -- run this inside 'nix develop'." >&2
        exit 2
    }

    if [ -x "$SAFETYLAB" ] && [ -z "$(find src/SafetyLab -name '*.cs' -newer "$SAFETYLAB" -print -quit)" ]; then
        return 0
    fi

    echo "== building safetylab ==" >&2
    dotnet publish src/SafetyLab -c Release -o "$(dirname "$SAFETYLAB")" -v q --nologo >&2 || {
        echo "could not build $SAFETYLAB" >&2
        exit 2
    }
}

# Which arm is deployed, read from the deployment itself rather than from an argument. The one
# thing that must never happen is measuring one backend and filing it as the other, and the
# deployment is the only source of truth both the app and these scripts can agree on.
sim_backend() {
    if [ -n "${SIM_BACKEND:-}" ]; then
        echo "$SIM_BACKEND"
        return 0
    fi

    local value
    value=$($K get "$(sim_workload)" \
        -o jsonpath='{range .spec.template.spec.containers[0].env[?(@.name=="SIM_BACKEND")]}{.value}{end}' \
        2>/dev/null)

    if [ -n "$value" ]; then
        echo "$value"
        return 0
    fi

    # No workload declares it. On the replicated arm the monitor is deployed BEFORE churnsim
    # (the cluster is formed through it), so fall back to the store that is present. A cluster
    # with no workload and no non-PostgreSQL store, or a deployment predating the RavenDB arm,
    # was PostgreSQL.
    if $K get statefulset ravendb >/dev/null 2>&1 || $K get deployment ravendb >/dev/null 2>&1; then
        echo "ravendb"
    elif $K get deployment mysql >/dev/null 2>&1; then
        echo "mysql"
    else
        echo "postgres"
    fi
}

# Which workload runs churnsim. The single-store arms are a Deployment; the replicated RavenDB
# arm is a StatefulSet, because each of its pods is pinned to one store member by ordinal and a
# Deployment's pods have no ordinal. Answers `deployment/churnsim` when neither exists, so a
# caller's error message names the thing it expected rather than an empty string.
sim_workload() {
    if $K get statefulset churnsim >/dev/null 2>&1; then
        echo "statefulset/churnsim"
    else
        echo "deployment/churnsim"
    fi
}

# single: one store pod (k8s/postgres.yaml or k8s/ravendb.yaml). cluster: the three-member
# RavenDB StatefulSet (k8s/ravendb-cluster.yaml). Read from the cluster, never passed in, for the
# same reason as sim_backend: a monitor deployed for the wrong topology reads one member of three
# and every partition check passes over a view that never disagreed with itself.
sim_topology() {
    if $K get statefulset ravendb >/dev/null 2>&1; then
        echo "cluster"
    else
        echo "single"
    fi
}

# Wait until `count` pods with the label exist and every one of them is Ready. `kubectl rollout
# status` cannot do this for a StatefulSet with the OnDelete update strategy (it refuses outright),
# and `kubectl wait` errors when the pods do not exist yet, so the two are staged.
wait_ready() {
    # Four statements, not one: bash expands every word of a `local` line before assigning any of
    # them, so `deadline` would read an unset `timeout` under set -u (docs/harness-traps.md, Shell).
    local label="$1"
    local count="$2"
    local timeout="${3:-300}"
    local deadline=$(( $(date +%s) + timeout ))
    while [ "$($K get pods -l "$label" --no-headers 2>/dev/null | wc -l)" -lt "$count" ]; do
        [ "$(date +%s)" -lt "$deadline" ] || { echo "wait_ready: fewer than $count pods with $label after ${timeout}s" >&2; return 1; }
        sleep 2
    done
    $K wait --for=condition=Ready pod -l "$label" --timeout="${timeout}s"
}

# Replace every churnsim pod and wait for the replacements. `rollout restart` is the Deployment
# way; the StatefulSet uses OnDelete (so a partition run's pods are never replaced under it) and
# is bounced by deleting its pods outright.
bounce_workload() {
    local workload
    workload=$(sim_workload)
    case "$workload" in
        statefulset/*)
            $K delete pod -l app=churnsim --wait=true
            wait_ready app=churnsim "$($K get "$workload" -o jsonpath='{.spec.replicas}')" 300
            ;;
        *)
            $K rollout restart "$workload"
            $K rollout status "$workload" --timeout=300s
            ;;
    esac
}

# A LIVE pg pod. Exec-ing into a terminating predecessor fails with "cannot exec into a container
# in a completed pod", which reads as "the store is unreachable".
_pgpod() { "$SAFETYLAB" pick-pod --label app=pg; }

# psql's stderr is NOT discarded. Sent to /dev/null, an unreachable store, a missing schema and a
# syntax error all return the empty string -- which every caller then reads as a real answer.
_psql() {
    local pod
    pod=$(_pgpod) || { echo "backend.sh: no live PostgreSQL pod to query" >&2; return 2; }
    $K exec "$pod" -- psql -U postgres -d churnsim -qAt -c "$1"
}

# A LIVE mysql pod, for the same reason as _pgpod.
_mysqlpod() { "$SAFETYLAB" pick-pod --label app=mysql; }

# The mysql client inside the store pod. Three things here are load-bearing:
#
#   -N -B   batch mode with no column names: tab-separated rows, which is the same shape psql -qAt
#           with an explicit chr(9) produces, so every caller parses one format. It also escapes a
#           tab inside a value rather than emitting it raw.
#   MYSQL_PWD, not -p  -- `-p<pass>` makes the client warn "Using a password on the command line
#           interface can be insecure" on stderr, and stderr is NOT discarded here (see _psql).
#           The password is read from the container's own MYSQL_ROOT_PASSWORD, so this file holds
#           no second copy of it.
#   "$1"    the SQL is a positional argument to sh, never interpolated into the -c string. A query
#           containing a quote would otherwise be reassembled into a different query.
_mysql() {
    local pod
    pod=$(_mysqlpod) || { echo "backend.sh: no live MySQL pod to query" >&2; return 2; }
    $K exec "$pod" -- sh -c 'export MYSQL_PWD="$MYSQL_ROOT_PASSWORD"; exec mysql -u root -N -B -e "$1"' \
        backend.sh "$1"
}

# `safetylab query` inside the monitor pod -- the only container that can talk to RavenDB in these
# terms, so the RavenDB arm needs `./scripts/monitor.sh deploy` before it can be measured at all.
_raven() {
    # A LIVE pod: a restarted Deployment leaves its predecessor in Failed/Succeeded for a while.
    local pod
    pod=$($K get pod -l app=safetylab \
            --field-selector=status.phase=Running \
            -o jsonpath='{range .items[?(@.status.containerStatuses[0].ready==true)]}{.metadata.name}{"\n"}{end}' \
            2>/dev/null | head -1)
    if [ -z "$pod" ]; then
        echo "backend.sh: the RavenDB arm reads the store through the safetylab pod, and there is none." >&2
        echo "            run ./scripts/monitor.sh deploy first." >&2
        return 2
    fi
    $K exec "$pod" -- dotnet safetylab.dll "$@"
}

db_placed() {
    case "$(sim_backend)" in
        ravendb) _raven query placed | tr -d '[:space:]' ;;
        mysql) _mysql "select count(*) from wolverine.wolverine_node_assignments where id like 'sim://%';" \
               | tr -d '[:space:]' ;;
        *) _psql "select count(*) from wolverine.wolverine_node_assignments where id like 'sim://%';" \
               | tr -d '[:space:]' ;;
    esac
}

db_nodes() {
    case "$(sim_backend)" in
        ravendb) _raven query nodes ;;
        mysql) _mysql "select node_number, description
                    from wolverine.wolverine_nodes order by node_number;" ;;
        *) _psql "select node_number || chr(9) || description
                    from wolverine.wolverine_nodes order by node_number;" ;;
    esac
}

db_per_node() {
    case "$(sim_backend)" in
        ravendb) _raven query per-node ;;
        mysql) _mysql "select node_id, count(*)
                    from wolverine.wolverine_node_assignments group by node_id;" ;;
        *) _psql "select node_id || chr(9) || count(*)
                    from wolverine.wolverine_node_assignments group by node_id;" ;;
    esac
}

db_records() {
    case "$(sim_backend)" in
        ravendb) _raven query records ;;
        mysql) _mysql "select event_name, count(*)
                    from wolverine.wolverine_node_records group by event_name order by count(*) desc;" ;;
        *) _psql "select event_name || chr(9) || count(*)
                    from wolverine.wolverine_node_records group by event_name order by count(*) desc;" ;;
    esac
}

db_per_minute() {
    local event="${1:-AssignmentChanged}"
    case "$(sim_backend)" in
        ravendb) _raven query per-minute --event "$event" ;;
        # `timestamp` is backticked: it is a non-reserved keyword in MySQL and parses bare today,
        # but a column named after a type is exactly the thing a server version bump reclassifies.
        mysql) _mysql "select date_format(\`timestamp\`, '%Y-%m-%d %H:%i'), count(*)
                    from wolverine.wolverine_node_records
                   where event_name = '$event'
                   group by 1 order by 1;" ;;
        *) _psql "select to_char(date_trunc('minute', timestamp), 'YYYY-MM-DD HH24:MI') || chr(9) || count(*)
                    from wolverine.wolverine_node_records
                   where event_name = '$event'
                   group by date_trunc('minute', timestamp) order by 1;" ;;
    esac
}

db_reset_metrics() {
    case "$(sim_backend)" in
        ravendb) _raven admin reset-metrics ;;
        mysql) _mysql "truncate table wolverine.wolverine_node_records;" ;;
        *) _psql "truncate wolverine.wolverine_node_records;" ;;
    esac
}

db_drop() {
    case "$(sim_backend)" in
        ravendb) _raven admin drop-database ;;
        # On MySQL the schema IS a database, so the PostgreSQL `drop schema ... cascade` is a
        # `drop database`. The app connects to `churnsim`, NOT to this one (see
        # src/ChurnSim/MySqlBackend.cs), which is what makes dropping it survivable: the bounced
        # pods reconnect and Weasel recreates it.
        mysql) _mysql "drop database if exists wolverine;" ;;
        *) _psql "drop schema if exists wolverine cascade;" ;;
    esac
}
