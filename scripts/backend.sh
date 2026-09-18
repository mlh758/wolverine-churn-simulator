#!/usr/bin/env bash
# Sourced by the measurement scripts. Not executable on its own.
#
# DEPENDS ON  a deployed store. The RavenDB path additionally needs a live safetylab pod
#             (./scripts/monitor.sh deploy) and returns 2 with that instruction when there is none.
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
#   sim_backend            -> postgres | ravendb
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
    value=$($K get deployment churnsim \
        -o jsonpath='{range .spec.template.spec.containers[0].env[?(@.name=="SIM_BACKEND")]}{.value}{end}' \
        2>/dev/null)

    # A deployment predating the RavenDB arm carries no SIM_BACKEND and was PostgreSQL.
    echo "${value:-postgres}"
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
        *) _psql "select count(*) from wolverine.wolverine_node_assignments where id like 'sim://%';" \
               | tr -d '[:space:]' ;;
    esac
}

db_nodes() {
    case "$(sim_backend)" in
        ravendb) _raven query nodes ;;
        *) _psql "select node_number || chr(9) || description
                    from wolverine.wolverine_nodes order by node_number;" ;;
    esac
}

db_per_node() {
    case "$(sim_backend)" in
        ravendb) _raven query per-node ;;
        *) _psql "select node_id || chr(9) || count(*)
                    from wolverine.wolverine_node_assignments group by node_id;" ;;
    esac
}

db_records() {
    case "$(sim_backend)" in
        ravendb) _raven query records ;;
        *) _psql "select event_name || chr(9) || count(*)
                    from wolverine.wolverine_node_records group by event_name order by count(*) desc;" ;;
    esac
}

db_per_minute() {
    local event="${1:-AssignmentChanged}"
    case "$(sim_backend)" in
        ravendb) _raven query per-minute --event "$event" ;;
        *) _psql "select to_char(date_trunc('minute', timestamp), 'YYYY-MM-DD HH24:MI') || chr(9) || count(*)
                    from wolverine.wolverine_node_records
                   where event_name = '$event'
                   group by date_trunc('minute', timestamp) order by 1;" ;;
    esac
}

db_reset_metrics() {
    case "$(sim_backend)" in
        ravendb) _raven admin reset-metrics ;;
        *) _psql "truncate wolverine.wolverine_node_records;" ;;
    esac
}

db_drop() {
    case "$(sim_backend)" in
        ravendb) _raven admin drop-database ;;
        *) _psql "drop schema if exists wolverine cascade;" ;;
    esac
}
