#!/usr/bin/env bash
# Sets the desired state of a cloud environment in git and pushes it. The Deploy workflow reconciles on push:
# "off" scales everything to zero and deletes the load balancer (shutdown.sh); "on" deploys main again.
#
#   infrastructure/k8s/set-state.sh beta off
#   infrastructure/k8s/set-state.sh beta on
#
# Only infrastructure/k8s/overlays/<env>/state is committed; other local changes are left alone. The repository
# variable DEPLOY_ENABLED must be "true" for the push to do anything (docs/OWNER_TODO.md, step 6).
set -euo pipefail

environment="${1:?environment (beta, prod)}"
state="${2:?state (on, off)}"
case "$environment" in beta|prod) ;; *) echo "Unknown environment '${environment}': beta or prod." >&2; exit 1 ;; esac
case "$state" in on|off) ;; *) echo "Unknown state '${state}': on or off." >&2; exit 1 ;; esac

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
file="infrastructure/k8s/overlays/${environment}/state"
cd "$root"

if [ "$(tr -d '[:space:]' < "$file" 2>/dev/null || true)" = "$state" ] && git diff --quiet -- "$file"; then
  echo "${environment} is already '${state}' on this branch (${file})."
  exit 0
fi

git pull --ff-only --quiet
echo "$state" > "$file"
git add "$file"
git commit --quiet -m "${environment} ${state}" -- "$file"
git push --quiet
echo "Pushed: ${environment} ${state}. The Deploy workflow applies it:"
echo "  gh run list --workflow Deploy --limit 1"
