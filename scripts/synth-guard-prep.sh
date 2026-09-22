#!/usr/bin/env bash
# Point the app at a non-superuser role, so the synth-guard RLS fault actually applies to it.
#
# DEPENDS ON  the PostgreSQL arm, $SAFETYLAB.
# REQUIRES    a deployed cluster. RUN IT AFTER EVERY DEPLOY: applying the manifest reverts
#             POSTGRES_CONNECTION to the postgres user, and row-level security never applies to a
#             superuser -- so the fault would silently do nothing.
# PRODUCES    the churn_app role, a deployment pointed at it with SIM_STALE_NODE_TIMEOUT_SECONDS=4
#             and SIM_JSON_LOGS=true, and bounced pods. MUTATES the cluster.
#
# ARGUMENTS   none.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

# PostgreSQL only, and not portably so. The fault injected here is row-level security hiding one
# node's row from the app role -- a Postgres feature neither other store has a counterpart for
# (RavenDB has nothing like it; MySQL has no row-level security at all, and its nearest equivalent
# would be a view swapped under the app's role, which is a different fault). Refuse rather than run
# something that looks like the experiment and is not.
#
# Written as "not postgres" and not as "is ravendb": an arm added later must be refused by default
# rather than fall through this gate into a run that measures nothing.
source scripts/backend.sh
if [ "$(sim_backend)" != "postgres" ]; then
    echo "$(basename "$0"): the synthetic-self-guard runs inject faults with Postgres row-level" >&2
    echo "    security, which no other store here has an equivalent of. This experiment is" >&2
    echo "    PostgreSQL-only, and the deployed arm is '$(sim_backend)'." >&2
    exit 2
fi

require_safetylab
PGPOD=$("$SAFETYLAB" pick-pod --label app=pg) || exit 2

$K exec "$PGPOD" -- psql -U postgres -d churnsim -qAt -c "
  do \$\$
  begin
    if not exists (select 1 from pg_roles where rolname = 'churn_app') then
      create role churn_app login password 'churn' nosuperuser;
    end if;
  end \$\$;
  grant connect, create on database churnsim to churn_app;
  grant usage, create on schema wolverine to churn_app;
  grant all on all tables in schema wolverine to churn_app;
  grant all on all sequences in schema wolverine to churn_app;
  alter default privileges for role postgres in schema wolverine grant all on tables to churn_app;
  alter default privileges for role postgres in schema wolverine grant all on sequences to churn_app;"

$K set env deployment/churnsim \
  POSTGRES_CONNECTION="Host=pg;Database=churnsim;Username=churn_app;Password=churn" \
  SIM_STALE_NODE_TIMEOUT_SECONDS=4 SIM_JSON_LOGS=true
# A pod already crash-looping on the old permissions sits in backoff; kick it so the rollout
# resumes immediately instead of waiting out the backoff window.
$K delete pod -l app=churnsim --field-selector=status.phase!=Running --ignore-not-found >/dev/null 2>&1 || true
$K rollout status deployment/churnsim --timeout=600s

echo "prep complete: app running as churn_app against the existing (postgres-owned) schema"
