#!/usr/bin/env bash
# Deploys one cloud environment: Cloud SQL backup, migrations (Kubernetes Job), then the API, worker, redaction, and the
# two web apps (fan web app, promoter dashboard).
#
#   infrastructure/k8s/deploy.sh <beta|prod> <image-tag>
#
# Needs kubectl pointed at the environment's cluster (gcloud container clusters get-credentials), kustomize, and
# gcloud signed in to the environment's project (for the backup). The image tag is the git commit the images were
# built from (see .github/workflows/deploy.yml). dev is not deployed: it runs locally (docs/DEVELOPMENT.md).
#
# Environment variables:
#   IMAGE_REPOSITORY   registry prefix instead of the overlay's (e.g. us-central1-docker.pkg.dev/<project>/fandomscore)
#   GCP_PROJECT_ID     project for the backup instead of the one in the overlay's image repository
#   SKIP_BACKUP=1      no Cloud SQL backup before migrating (the first deploy of an empty database)
#   SKIP_HEALTH_CHECK=1  don't call the public health endpoint afterwards (before DNS and the certificate exist)
#
# If the overlay has a `settings` file (key=value lines), those app_settings rows are written after the migrations. If it
# has a `seed` file, the demo data from tools/dev/seed.py is written too (once; later deploys find it present and do nothing).
set -euo pipefail

environment="${1:?environment (beta, prod)}"
tag="${2:?image tag}"
case "$environment" in
  beta|prod) ;;
  *) echo "Unknown environment '${environment}': beta or prod (dev runs locally, see docs/DEVELOPMENT.md)." >&2; exit 1 ;;
esac
here="$(cd "$(dirname "$0")" && pwd)"   # infrastructure/k8s, absolute: the script cds into the overlay below
root="$(cd "${here}/../.." && pwd)"
overlay="${here}/overlays/${environment}"
namespace=fandomscore
instance="fandomscore-${environment}"

cd "$overlay"
# Registry prefix from the overlay's api image (IMAGE_REPOSITORY overrides it), e.g.
# us-central1-docker.pkg.dev/project-ef36a8a9-d958-4163-b4c/fandomscore
repository="${IMAGE_REPOSITORY:-$(awk '/^  - name: api$/ { found = 1 } found && /newName:/ { print $2; exit }' kustomization.yaml | sed 's|/api$||')}"
[ -n "$repository" ] || { echo "Cannot find the image repository; set IMAGE_REPOSITORY." >&2; exit 1; }
project="${GCP_PROJECT_ID:-$(sed -n 's|.*-docker\.pkg\.dev/\([^/]*\)/.*|\1|p' <<<"$repository")}"
hostname="$(awk '/^kind: HTTPRoute$/ { found = 1 } found && /^    - / { print $2; exit }' gateway.yaml)"

# Leave the overlay as committed whatever happens (the tag is a deploy-time value).
trap 'git -C "$overlay" checkout -- kustomization.yaml 2>/dev/null || true' EXIT
for image in api worker migrate web promoter-web; do
  kustomize edit set image "${image}=${repository}/${image}:${tag}"
done

if [ "${SKIP_BACKUP:-0}" != 1 ]; then
  # Real users live here: keep a restore point from right before this commit's migrations run.
  echo "== Cloud SQL backup of ${project}/${instance} before migrating"
  gcloud sql backups create --instance "$instance" --project "$project" --description "pre-deploy ${tag}" --quiet
fi

echo "== Migrations (${environment}, ${tag})"
kubectl delete job migrate -n "$namespace" --ignore-not-found --wait
# Everything except the Deployments, HPA, and PDB: namespace, identities, config, secrets, migrate job, gateway.
kustomize build . | kubectl apply -l 'fandomscore.app/stage!=app' -f -
if ! kubectl wait job/migrate -n "$namespace" --for=condition=complete --timeout=10m; then
  echo "Migration job did not complete; logs:" >&2
  kubectl logs job/migrate -n "$namespace" --all-containers >&2 || true
  echo "The running version is untouched. Fix the migration, or restore the pre-deploy backup (infrastructure/README.md)." >&2
  exit 1
fi
kubectl logs job/migrate -n "$namespace"

# Environment settings (app_settings, migration 0051): the overlay's `settings` file holds key=value lines, written
# every deploy so the file is the source of truth. Keys and values are plain identifiers (the table checks too).
if [ -f "${overlay}/settings" ]; then
  echo "== Settings ($(grep -v '^#' "${overlay}/settings" | tr '\n' ' '))"
  grep -v '^#' "${overlay}/settings" | grep . | while IFS='=' read -r key value; do
    case "$key" in *[!a-z0-9_]*|'') echo "Bad settings key '${key}' in ${overlay}/settings" >&2; exit 1 ;; esac
    case "$value" in *[!a-z0-9_.:-]*|'') echo "Bad settings value '${value}' in ${overlay}/settings" >&2; exit 1 ;; esac
    printf "INSERT INTO app_settings (key, value) VALUES ('%s', '%s')\n  ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now() WHERE app_settings.value <> EXCLUDED.value;\n" "$key" "$value"
  done | "${here}/db-shell.sh" "$environment"
fi

# Demo data (tools/dev/seed.py, "Cloud demo data"): the overlay's `seed` file holds the script's arguments. The SQL
# is a no-op once the seed is present, so this runs every deploy and writes once. prod has no such file.
if [ -f "${overlay}/seed" ]; then
  echo "== Demo data ($(tr -d '\n' < "${overlay}/seed"))"
  # shellcheck disable=SC2046 # the file holds separate arguments
  python3 "${root}/tools/dev/seed.py" --sql --fake --if-absent --no-photos $(cat "${overlay}/seed") \
    | "${here}/db-shell.sh" "$environment"
fi

echo "== Rollout"
kustomize build . | kubectl apply -f -
kubectl rollout status deployment/api -n "$namespace" --timeout=10m
kubectl rollout status deployment/worker -n "$namespace" --timeout=10m
kubectl rollout status deployment/redaction -n "$namespace" --timeout=10m
kubectl rollout status deployment/web -n "$namespace" --timeout=10m
kubectl rollout status deployment/promoter-web -n "$namespace" --timeout=10m

if [ "${SKIP_HEALTH_CHECK:-0}" != 1 ] && [ -n "$hostname" ]; then
  echo "== Health check https://${hostname}/v1/health"
  # Up to 10 minutes: a freshly created Gateway takes several minutes before its HTTPS proxy exists (the HTTP
  # listener comes up first). An existing load balancer answers on the first try.
  healthy=0
  for attempt in $(seq 1 60); do
    if curl -fsS --max-time 10 "https://${hostname}/v1/health" >/dev/null; then healthy=1; break; fi
    [ $((attempt % 6)) -eq 0 ] && echo "   still waiting for https://${hostname} ($((attempt / 6)) min)"
    sleep 10
  done
  if [ "$healthy" != 1 ]; then
    echo "The API is not healthy after the rollout. Roll back: $0 ${environment} <previous-sha>" >&2
    exit 1
  fi
fi

# The web apps' hostnames work once their certificates are active and DNS points at the load balancer
# (infrastructure/README.md, "Web apps"); until then this only warns.
if [ "${SKIP_HEALTH_CHECK:-0}" != 1 ]; then
  for web in $(awk '/^  name: (web|promoter-web)$/ { found = 1; next } found && /^    - / { print $2; found = 0 }' gateway.yaml); do
    curl -fsS --max-time 10 "https://${web}/healthz" >/dev/null \
      && echo "== https://${web} is up" \
      || echo "Warning: https://${web}/healthz didn't answer (certificate or DNS not ready yet?)." >&2
  done
fi

echo "Deployed ${tag} to ${environment}."
