#!/usr/bin/env bash
# The version of each deployable component in one environment: the git commit its images are tagged with.
#
#   infrastructure/k8s/versions.sh <beta|prod>
#
# Prints one line per component: `<component> <commit> <auto|pinned> <image>...`, for example
#   backend 4867be8... auto api worker migrate
#   web a1b2c3d... pinned web
#   promoter-web 9f8e7d6... auto promoter-web
#
# The overlay's `versions.env` says, per component, `auto` or a commit:
# - auto: the last commit (up to HEAD) that changed a path the component's images are built from. A commit that
#   changes nothing in those paths leaves the version, so its images, its pods, and (for the backend) the backup
#   and migrate Job alone. The images at that commit and at HEAD are identical, so CI builds them from HEAD.
# - a commit: hold the component at that build (a rollback). Its images must already be in the registry.
# This is the one place that maps components to images and to source paths; deploy.sh and the Deploy workflow
# read it. Keep the paths in step with the root Dockerfile's COPY lines (and .dockerignore).
# Needs the full git history (a shallow clone can't tell when a path last changed).
set -euo pipefail

environment="${1:?environment (beta, prod)}"
case "$environment" in
  beta|prod) ;;
  *) echo "Unknown environment '${environment}': beta or prod." >&2; exit 1 ;;
esac
here="$(cd "$(dirname "$0")" && pwd)"
root="$(git -C "$here" rev-parse --show-toplevel)"
file="${here}/overlays/${environment}/versions.env"

components=(backend web promoter-web)

images() {
  case "$1" in
    # One build stage; the worker image also runs the redaction Deployment; testkit (migrate + seed for the apps'
    # end-to-end suites, tools/testkit) is published with the others and never deployed.
    backend) echo "api worker migrate testkit" ;;
    web) echo "web" ;;
    promoter-web) echo "promoter-web" ;;
  esac
}

# Everything the component's Dockerfile stages copy in, plus the Dockerfile and the build context's ignore file.
paths() {
  case "$1" in
    backend) echo "Dockerfile .dockerignore global.json Directory.Build.props packages/spotify packages/music-providers packages/storage packages/notifications database services/api workers tools/dev/seed.py tools/dev/spotify_connect.py tools/testkit" ;;
    web) echo "Dockerfile .dockerignore apps/mobile" ;;
    promoter-web) echo "Dockerfile .dockerignore apps/promoter-web" ;;
  esac
}

if [ "$(git -C "$root" rev-parse --is-shallow-repository)" = true ]; then
  echo "versions.sh needs the full git history (actions/checkout with fetch-depth: 0, or git fetch --unshallow)." >&2
  exit 1
fi

# versions.env: `component=auto` or `component=<commit>`; comments and blank lines ignored.
declare -A wanted=()
if [ -f "$file" ]; then
  while IFS='=' read -r key value; do
    key="$(tr -d '[:space:]' <<<"$key")"
    value="$(sed 's/#.*//' <<<"$value" | tr -d '[:space:]')"
    case "$key" in ''|'#'*) continue ;; esac
    [ -n "$(images "$key")" ] || { echo "Unknown component '${key}' in ${file} (${components[*]})." >&2; exit 1; }
    wanted[$key]="${value:-auto}"
  done < "$file"
fi

head="$(git -C "$root" rev-parse HEAD)"
for component in "${components[@]}"; do
  value="${wanted[$component]:-auto}"
  read -r -a component_paths <<<"$(paths "$component")"
  if [ "$value" = auto ]; then
    source=auto
    commit="$(git -C "$root" log -1 --format=%H HEAD -- "${component_paths[@]}")"
    # Guard against history shapes where "last commit that touched it" isn't the same content as HEAD.
    if [ -z "$commit" ] || ! git -C "$root" diff --quiet "$commit" "$head" -- "${component_paths[@]}"; then
      commit="$head"
    fi
  else
    source=pinned
    commit="$(git -C "$root" rev-parse --verify --quiet "${value}^{commit}")" \
      || { echo "${file}: ${component}=${value} is not a commit in this repository." >&2; exit 1; }
  fi
  echo "${component} ${commit} ${source} $(images "$component")"
done
