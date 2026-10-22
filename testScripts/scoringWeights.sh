#!/usr/bin/env bash
# Shows or changes the Spotify scoring weights in the LOCAL docker-compose database. Weights are relative shares
# (they need not add up to anything); saving one queues a rescore of every active artist, which the worker picks up.
# Outside local development, staff change weights with PUT /v1/admin/scoring-rules/{rule_key}.
# Usage:
#   testScripts/scoringWeights.sh                              # list the Spotify rules
#   testScripts/scoringWeights.sh spotify_liked_track 2        # set a weight
#   testScripts/scoringWeights.sh spotify_liked_track 2 50     # set a weight and the saturation count
set -euo pipefail
cd "$(dirname "$0")/.."
psql=(docker compose --env-file .env -f infrastructure/docker-compose.yml exec -T postgres
      psql -U fandom -d fandom_score -v ON_ERROR_STOP=1 -q)

if [[ $# -ge 2 ]]; then
  [[ "$1" =~ ^spotify_[a-z_]+$ ]] || { echo "Rule must be one of the spotify_* rules listed without arguments."; exit 1; }
  [[ "$2" =~ ^[0-9]+(\.[0-9]+)?$ ]] || { echo "Weight must be a number of 0 or more."; exit 1; }
  [[ -z "${3:-}" || "$3" =~ ^[0-9]+(\.[0-9]+)?$ ]] || { echo "Saturation must be a positive number."; exit 1; }
  updated=$("${psql[@]}" -At -v rule="$1" -v weight="$2" -v saturation="${3:-}" <<'SQL'
UPDATE scoring_rules SET points = :'weight'::numeric,
       saturation = COALESCE(NULLIF(:'saturation', '')::numeric, saturation)
 WHERE rule_key = :'rule' RETURNING rule_key;
SQL
)
  [[ -n "$updated" ]] || { echo "No rule named $1."; exit 1; }
  echo "Updated $1; every active artist is queued for rescoring."
fi

"${psql[@]}" -c "
SELECT rule_key, points AS weight,
       CASE WHEN active THEN round(100 * points / NULLIF(sum(points) FILTER (WHERE active) OVER (), 0), 1) END AS share_pct,
       saturation, active
  FROM scoring_rules WHERE rule_key LIKE 'spotify\_%' ORDER BY rule_key;"
