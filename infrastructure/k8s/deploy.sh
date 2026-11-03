#!/usr/bin/env bash
# Deploys one cloud environment, but only what changed. Each component runs the version versions.sh gives for it
# (the overlay's versions.env: `auto` = the last commit that changed what its images are built from, or a pinned
# commit for a rollback):
#   backend       api, worker (and redaction), migrate
#   web           fan web app
#   promoter-web  promoter dashboard
#
#   infrastructure/k8s/deploy.sh <beta|prod>
#
# - The backend version differs from the one running: Cloud SQL backup, migrate Job, settings, seed, then rollout.
# - Only a web app or a manifest differs: just the rollout. No backup, no migrations; other pods are untouched.
# - Nothing differs (images, manifests, the overlay's settings and seed files): says so and stops.
#
# Needs kubectl pointed at the environment's cluster (gcloud container clusters get-credentials), kustomize, the full
# git history (versions.sh), and gcloud signed in to the environment's project (backup, image checks). Every image
# must already be in the registry under its component's commit (the Deploy workflow builds the missing ones).
# dev is not deployed: it runs locally (docs/DEVELOPMENT.md).
#
# Environment variables:
#   DEPLOY_ALL=1       every step even if nothing changed: backup, migrate Job, settings, seed, rollout, health check
#                      (after db-wipe.sh, or to redo a deploy; a manual run of the Deploy workflow does this by default)
#   IMAGE_REPOSITORY   registry prefix instead of the overlay's (e.g. us-central1-docker.pkg.dev/<project>/fandomscore)
#   GCP_PROJECT_ID     project for the backup instead of the one in the overlay's image repository
#   SKIP_BACKUP=1      no Cloud SQL backup before migrating (the first deploy of an empty database)
#   SKIP_HEALTH_CHECK=1  don't call the public health endpoint afterwards (before DNS and the certificate exist)
#
# If the overlay has a `settings` file (key=value lines), those app_settings rows are written after the migrations. If it
# has a `seed` file, the demo data from tools/dev/seed.py is written too (once; later deploys find it present and do
# nothing). Both run when the backend changes, or when either file differs from the last deploy's (ConfigMap deploy-inputs).
set -euo pipefail

environment="${1:?environment (beta, prod)}"
case "$environment" in
  beta|prod) ;;
  *) echo "Unknown environment '${environment}': beta or prod (dev runs locally, see docs/DEVELOPMENT.md)." >&2; exit 1 ;;
esac
[ $# -eq 1 ] || { echo "Usage: $0 <beta|prod>. Versions come from overlays/${environment}/versions.env (versions.sh)." >&2; exit 1; }
here="$(cd "$(dirname "$0")" && pwd)"   # infrastructure/k8s, absolute: the script cds into the overlay below
root="$(cd "${here}/../.." && pwd)"
overlay="${here}/overlays/${environment}"
namespace=fandomscore
instance="fandomscore-${environment}"
deploy_all="${DEPLOY_ALL:-0}"

versions="$("${here}/versions.sh" "$environment")"

cd "$overlay"
# Registry prefix from the overlay's api image (IMAGE_REPOSITORY overrides it), e.g.
# us-central1-docker.pkg.dev/project-ef36a8a9-d958-4163-b4c/fandomscore
repository="${IMAGE_REPOSITORY:-$(awk '/^  - name: api$/ { found = 1 } found && /newName:/ { print $2; exit }' kustomization.yaml | sed 's|/api$||')}"
[ -n "$repository" ] || { echo "Cannot find the image repository; set IMAGE_REPOSITORY." >&2; exit 1; }
project="${GCP_PROJECT_ID:-$(sed -n 's|.*-docker\.pkg\.dev/\([^/]*\)/.*|\1|p' <<<"$repository")}"
hostname="$(awk '/^kind: HTTPRoute$/ { found = 1 } found && /^    - / { print $2; exit }' gateway.yaml)"

# The image a Deployment runs now (empty if it doesn't exist yet).
running() {
  kubectl get deployment "$1" -n "$namespace" -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null || true
}

# Leave the overlay as committed whatever happens (the versions are deploy-time values).
work="$(mktemp -d)"
trap 'git -C "$overlay" checkout -- kustomization.yaml 2>/dev/null || true; rm -rf "$work"' EXIT

echo "== Versions (${environment}, overlays/${environment}/versions.env)"
backend_changed=0
backend=""
while read -r component commit source images; do
  printf '   %-13s %s (%s)\n' "$component" "$commit" "$source"
  for image in $images; do
    kustomize edit set image "${image}=${repository}/${image}:${commit}"
    if ! gcloud artifacts docker images describe "${repository}/${image}:${commit}" --project "$project" >/dev/null 2>&1; then
      echo "${repository}/${image}:${commit} is not in the registry. Build it (the Deploy workflow builds 'auto'" >&2
      echo "versions), or pin ${component} to a commit whose images exist (versions.env)." >&2
      exit 1
    fi
  done
  if [ "$component" = backend ]; then
    backend="$commit"
    for deployment in api worker redaction; do
      image=worker; [ "$deployment" = api ] && image=api
      [ "$(running "$deployment")" = "${repository}/${image}:${commit}" ] || backend_changed=1
    done
  fi
done <<<"$versions"
[ "$deploy_all" = 1 ] && backend_changed=1

kustomize build . > "${work}/all.yaml"

# What the database steps read: the overlay's settings and seed files. Recorded in the cluster after they are written.
inputs=()
[ -f "${overlay}/settings" ] && inputs+=(--from-file=settings="${overlay}/settings")
[ -f "${overlay}/seed" ] && inputs+=(--from-file=seed="${overlay}/seed")
inputs_changed=0
if [ ${#inputs[@]} -gt 0 ]; then
  kubectl create configmap deploy-inputs -n "$namespace" "${inputs[@]}" --dry-run=client -o yaml > "${work}/inputs.yaml"
  kubectl diff -f "${work}/inputs.yaml" >/dev/null 2>&1 || inputs_changed=1
fi

# Without a backend change the migrate Job is left alone (it would run the migrations again once its TTL removed it).
only=()
[ "$backend_changed" = 1 ] || only=(-l 'fandomscore.app/stage!=migrate')

if [ "$backend_changed" = 0 ] && [ "$inputs_changed" = 0 ]; then
  # kubectl diff: 0 = no differences, 1 = differences, more = it couldn't tell (then deploy, to be safe).
  status=0
  kubectl diff ${only[@]+"${only[@]}"} -f "${work}/all.yaml" > "${work}/diff" 2>&1 || status=$?
  if [ "$status" = 0 ]; then
    echo "Nothing to deploy to ${environment}: every component runs its version and the manifests match."
    exit 0
  fi
  [ "$status" = 1 ] || { echo "kubectl diff failed (exit ${status}); deploying anyway:" >&2; cat "${work}/diff" >&2; }
  echo "== Changes (kubectl diff, without the backend: no backup, no migrations)"
  grep -E '^diff |^[-+] +image:|^[-+] +replicas:' "${work}/diff" || true
fi

if [ "$backend_changed" = 1 ]; then
  if [ "${SKIP_BACKUP:-0}" != 1 ]; then
    # Keep a restore point from right before this version's migrations run.
    echo "== Cloud SQL backup of ${project}/${instance} before migrating"
    gcloud sql backups create --instance "$instance" --project "$project" --description "pre-deploy ${backend}" --quiet
  fi

  echo "== Migrations (${environment}, backend ${backend})"
  kubectl delete job migrate -n "$namespace" --ignore-not-found --wait
  # Everything except the Deployments, HPA, and PDB: namespace, identities, config, secrets, migrate job, gateway.
  kubectl apply -l 'fandomscore.app/stage!=app' -f "${work}/all.yaml"
  if ! kubectl wait job/migrate -n "$namespace" --for=condition=complete --timeout=10m; then
    echo "Migration job did not complete; logs:" >&2
    kubectl logs job/migrate -n "$namespace" --all-containers >&2 || true
    echo "The running version is untouched. Fix the migration, or restore the pre-deploy backup (infrastructure/README.md)." >&2
    exit 1
  fi
  kubectl logs job/migrate -n "$namespace"
fi

if [ "$backend_changed" = 1 ] || [ "$inputs_changed" = 1 ]; then
  # Environment settings (app_settings, migration 0051): the overlay's `settings` file holds key=value lines, written
  # every time so the file is the source of truth. Keys and values are plain identifiers (the table checks too).
  if [ -f "${overlay}/settings" ]; then
    echo "== Settings ($(grep -v '^#' "${overlay}/settings" | tr '\n' ' '))"
    grep -v '^#' "${overlay}/settings" | grep . | while IFS='=' read -r key value; do
      case "$key" in *[!a-z0-9_]*|'') echo "Bad settings key '${key}' in ${overlay}/settings" >&2; exit 1 ;; esac
      case "$value" in *[!a-z0-9_.:-]*|'') echo "Bad settings value '${value}' in ${overlay}/settings" >&2; exit 1 ;; esac
      printf "INSERT INTO app_settings (key, value) VALUES ('%s', '%s')\n  ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now() WHERE app_settings.value <> EXCLUDED.value;\n" "$key" "$value"
    done | "${here}/db-shell.sh" "$environment"
  fi

  # Demo data (tools/dev/seed.py, "Cloud demo data"): the overlay's `seed` file holds the script's arguments. The SQL
  # is a no-op once the seed is present, so it writes once. prod has no such file.
  if [ -f "${overlay}/seed" ]; then
    echo "== Demo data ($(tr -d '\n' < "${overlay}/seed"))"
    # Real artists from Spotify when the workflow found the client secret (Spotify__ClientSecret in the environment);
    # the client id is the overlay's. Otherwise made-up ones.
    Spotify__ClientId="$(sed -n 's/^ *- Spotify__ClientId=//p' kustomization.yaml | tr -d '[:space:]')"
    export Spotify__ClientId
    artists=--fake
    if [ -n "${Spotify__ClientSecret:-}" ] && [ -n "$Spotify__ClientId" ]; then artists=; echo "   artists: real, from Spotify"; else echo "   artists: made up (no Spotify credentials here)"; fi
    # shellcheck disable=SC2046,SC2086 # the file holds separate arguments; $artists is one flag or nothing
    python3 "${root}/tools/dev/seed.py" --sql $artists --if-absent --no-photos $(cat "${overlay}/seed") \
      | "${here}/db-shell.sh" "$environment"
  fi

  [ ${#inputs[@]} -eq 0 ] || kubectl apply -f "${work}/inputs.yaml"
fi

echo "== Rollout"
kubectl apply ${only[@]+"${only[@]}"} -f "${work}/all.yaml"
# Unchanged Deployments report "successfully rolled out" at once.
for deployment in api worker redaction web promoter-web; do
  kubectl rollout status "deployment/${deployment}" -n "$namespace" --timeout=10m
done

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
    echo "The API is not healthy after the rollout. Roll back the component that changed: pin it in" >&2
    echo "overlays/${environment}/versions.env to the commit it ran before (infrastructure/README.md, \"Deploying\")." >&2
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

echo "Deployed to ${environment}:"
while read -r component commit source _; do printf '   %-13s %s (%s)\n' "$component" "$commit" "$source"; done <<<"$versions"
