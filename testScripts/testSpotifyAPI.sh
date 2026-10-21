#!/usr/bin/env bash
# Adds a test artist and connects your Spotify account to the local API (tools/dev/spotify_connect.py).
# Needs the compose database, API, and worker running. Run from anywhere.
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
python3 tools/dev/spotify_connect.py track-artist "066X20Nz7iquqkkCW6Jxy6"
python3 tools/dev/spotify_connect.py connect
