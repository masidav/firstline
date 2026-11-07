#!/usr/bin/env bash
# Entrypoint of the testkit image (Dockerfile target `testkit`): waits for PostgreSQL, applies every migration
# (database/, the same build as the migrate image), loads the development seed (tools/dev/seed.py), and exits.
# One run gives the apps' end-to-end suites a database in the state the backend expects at this version
# (tools/testkit/compose.yml starts it before the API; docs/plans/2026-10-31-repo-split.md).
#
# Settings: PGHOST, PGPORT, PGDATABASE, PGUSER, PGPASSWORD (psql's variables) name the database; the migrator's
# ConnectionStrings__Database is derived from them unless set. SEED_ARGS (default `--fake`) are seed.py's arguments
# besides --sql; `--fake` keeps Spotify out of it (made-up artists), as CI does.
set -euo pipefail

: "${PGHOST:?PGHOST (the PostgreSQL host) is required}"
: "${PGDATABASE:?PGDATABASE is required}"
: "${PGUSER:?PGUSER is required}"
export PGPORT="${PGPORT:-5432}"
export ConnectionStrings__Database="${ConnectionStrings__Database:-Host=${PGHOST};Port=${PGPORT};Database=${PGDATABASE};Username=${PGUSER};Password=${PGPASSWORD:-}}"

deadline=$(( $(date +%s) + 60 ))
until pg_isready -q; do
  if [ "$(date +%s)" -ge "$deadline" ]; then echo "PostgreSQL at ${PGHOST}:${PGPORT} did not answer within 60 s." >&2; exit 1; fi
  sleep 1
done

echo "== migrations"
dotnet /app/migrate/FandomScore.Database.dll

echo "== seed"
read -r -a seed_args <<<"${SEED_ARGS:---fake}"
python3 /app/tools/dev/seed.py --sql "${seed_args[@]}" | psql -q -v ON_ERROR_STOP=1
echo "Database ${PGDATABASE} migrated and seeded."
