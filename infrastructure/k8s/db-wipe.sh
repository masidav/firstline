#!/usr/bin/env bash
# Deletes ALL data in a cloud environment's database: every table, function, and type the application user owns,
# including the migration journal, so the next deploy re-applies every migration into an empty schema, writes the
# overlay's settings, and seeds again (deploy.sh). The ledger refuses DELETE and TRUNCATE by design, so this is the
# only way to start over; real accounts are gone too (people sign in again; demo accounts are furnished again).
#
#   infrastructure/k8s/db-wipe.sh beta      # asks you to type "wipe beta"
#
# beta only on purpose: prod data is never wiped. Needs kubectl pointed at the beta cluster (db-shell.sh).
set -euo pipefail

env="${1:?usage: db-wipe.sh beta}"
[ "$env" = beta ] || { echo "Only beta can be wiped." >&2; exit 2; }
read -r -p "Delete ALL data in the ${env} database (accounts, demo data, everything)? Type 'wipe ${env}' to continue: " answer
[ "$answer" = "wipe ${env}" ] || { echo "Cancelled."; exit 1; }

"$(dirname "$0")/db-shell.sh" "$env" <<'SQL'
SET client_min_messages = warning;
DROP OWNED BY CURRENT_USER CASCADE;
SELECT count(*) AS tables_left FROM pg_tables WHERE schemaname = 'public';
SQL
echo "${env} is empty. Deploy to rebuild it: gh workflow run Deploy --repo masidav/firstline -f environment=${env}"
