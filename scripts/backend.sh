#!/usr/bin/env bash
# Sourced by the measurement scripts. Not executable on its own.
#
# Every measurement in this rig is "ask the store a question, get tab-separated text back". On
# PostgreSQL that is psql inside the pg pod. RavenDB has no psql and its container ships no
# client worth depending on, so the equivalent is `safetylab query` inside the already-deployed
# monitor pod, which speaks RavenDB's REST API and takes no Wolverine dependency.
#
# The PostgreSQL path below is character-for-character what measure.sh and duplicate-rate.sh
# already ran. That is deliberate: every number in RESULTS.md was taken through those exact
# queries, and a refactor that quietly reworded them would break comparability with the entire
# existing record for no gain.
#
#   source scripts/backend.sh
#   sim_backend            -> postgres | ravendb
#   db_assigned            -> agentUri <TAB> pod name, sim:// agents only
#   db_placed              -> count of placed sim:// agents
#   db_nodes               -> node number <TAB> description (= pod name)
#   db_per_node            -> node id <TAB> agent count
#   db_records             -> record type <TAB> count, descending
#   db_per_minute [event]  -> minute <TAB> count (default AssignmentChanged)
#   db_reset_metrics       -> clear the node-record history, leaving the cluster running
#   db_drop                -> drop the whole store, for switching Wolverine builds

export PATH="$HOME/.local/bin:$PATH"
: "${K:=minikube kubectl -- --context=minikube}"

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

_pgpod() { $K get pod -l app=pg -o jsonpath='{.items[0].metadata.name}'; }

_psql() { $K exec "$(_pgpod)" -- psql -U postgres -d churnsim -qAt -c "$1" 2>/dev/null; }

# `safetylab query` inside the monitor pod. The monitor is the only container in the cluster that
# can talk to RavenDB in these terms, so the RavenDB arm needs `./scripts/monitor.sh deploy` before
# it can be measured at all -- which is worth saying out loud rather than failing obscurely.
_raven() {
    # A LIVE pod, not `.items[0]`. A restarted Deployment leaves the previous pod behind in
    # Failed/Succeeded for a while, and exec-ing into it fails with "cannot exec into a container
    # in a completed pod" -- which reads as "the store is unreachable" at exactly the moment a
    # measurement is being taken. Same selector trap that live_pods.py exists for.
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

db_assigned() {
    case "$(sim_backend)" in
        ravendb) _raven query assigned ;;
        *) _psql "select a.id || chr(9) || n.description
                    from wolverine.wolverine_node_assignments a
                    join wolverine.wolverine_nodes n on n.id = a.node_id
                   where a.id like 'sim://%';" ;;
    esac
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
                    from wolverine.wolverine_node_assignments group by 1;" ;;
    esac
}

db_records() {
    case "$(sim_backend)" in
        ravendb) _raven query records ;;
        *) _psql "select event_name || chr(9) || count(*)
                    from wolverine.wolverine_node_records group by 1 order by 2 desc;" ;;
    esac
}

db_per_minute() {
    local event="${1:-AssignmentChanged}"
    case "$(sim_backend)" in
        ravendb) _raven query per-minute --event "$event" ;;
        *) _psql "select to_char(date_trunc('minute', timestamp), 'YYYY-MM-DD HH24:MI') || chr(9) || count(*)
                    from wolverine.wolverine_node_records
                   where event_name = '$event'
                   group by 1 order by 1;" ;;
    esac
}

db_reset_metrics() {
    case "$(sim_backend)" in
        ravendb) _raven admin reset-metrics ;;
        *) $K exec "$(_pgpod)" -- psql -U postgres -d churnsim -c \
               "truncate wolverine.wolverine_node_records;" ;;
    esac
}

db_drop() {
    case "$(sim_backend)" in
        ravendb) _raven admin drop-database ;;
        *) $K exec "$(_pgpod)" -- psql -U postgres -d churnsim -c \
               "drop schema if exists wolverine cascade;" ;;
    esac
}
