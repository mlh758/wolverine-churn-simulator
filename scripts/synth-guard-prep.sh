#!/usr/bin/env bash
# Prep for the synth-guard RLS chaos runs: the app must connect as a NON-superuser role, because
# Postgres row-level security never applies to superusers and the manifest's default connection is
# `postgres`. Creates the churn_app role, points the deployment at it, and resets the schema so the
# app recreates it cleanly under the new role. Run once after each deploy.sh (deploy.sh re-applies
# the manifest, which reverts POSTGRES_CONNECTION to the postgres user).
#
#   ./scripts/synth-guard-prep.sh
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:$PATH"
K="minikube kubectl -- --context=minikube"

PGPOD=$($K get pod -l app=pg -o jsonpath='{.items[0].metadata.name}')

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
