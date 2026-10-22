#!/usr/bin/env bash
# Runs the fan app in the browser against the local API. Run from anywhere.
# Writes apps/mobile/.env.local from the root .env (Spotify client ID, dev sign-in) unless one already exists.
set -euo pipefail
cd "$(dirname "$0")/.."

if [ ! -f apps/mobile/.env.local ]; then
  client_id=$(grep -E '^Spotify__ClientId=' .env 2>/dev/null | cut -d= -f2- | tr -d "'\"" || true)
  cat > apps/mobile/.env.local <<ENV
# Written by testScripts/startWebAppLocal.sh from the root .env. Edit freely; it is not overwritten.
EXPO_PUBLIC_API_URL=http://127.0.0.1:5271/v1
EXPO_PUBLIC_DEV_SIGN_IN=true
EXPO_PUBLIC_SPOTIFY_CLIENT_ID=${client_id}
ENV
  echo "Wrote apps/mobile/.env.local (Spotify client ID ${client_id:+set}${client_id:-EMPTY: set Spotify__ClientId in .env})."
fi

echo "Open the app at http://127.0.0.1:8081 (not localhost): connecting Spotify redirects back to"
echo "http://127.0.0.1:8081/spotify-callback, which must be in the Spotify dashboard and in Spotify__RedirectUris."
cd apps/mobile
npm install && npm run web
