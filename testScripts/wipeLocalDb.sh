#!/usr/bin/env bash
# Deletes ALL data in the LOCAL docker-compose database, then re-applies the migrations, leaving an empty schema.
# It drops the whole schema (score_events refuses DELETE and TRUNCATE by design, so emptying tables won't work).
# Local development only: it runs inside the compose Postgres container, never against another database.
# Usage: testScripts/wipeLocalDb.sh [--yes]
set -euo pipefail
cd "$(dirname "$0")/.."
compose=(docker compose --env-file .env -f infrastructure/docker-compose.yml)

if [[ "${1:-}" != "--yes" ]]; then
  read -r -p "Delete ALL data in the local fandom_score database? Type 'wipe' to continue: " answer
  [[ "$answer" == "wipe" ]] || { echo "Cancelled."; exit 1; }
fi

"${compose[@]}" exec -T postgres psql -U fandom -d fandom_score -v ON_ERROR_STOP=1 -q \
  -c "SET client_min_messages = warning; DROP SCHEMA public CASCADE; CREATE SCHEMA public AUTHORIZATION fandom;"

set -a; . ./.env; set +a
dotnet run --project database
echo "Local database wiped; migrations re-applied. Restart the API and worker if they are running."
