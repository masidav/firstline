#!/usr/bin/env bash
# Runs the fan app in the browser against the local API. Run from anywhere.
set -euo pipefail
cd "$(dirname "$0")/../apps/mobile"
cp .env.example .env.local && npm install && npm run web
