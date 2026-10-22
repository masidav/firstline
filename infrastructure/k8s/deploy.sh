#!/usr/bin/env bash
# Deploys one environment: migrations first (Kubernetes Job), then the API and worker.
#
#   infrastructure/k8s/deploy.sh <dev|staging|prod> <image-tag>
#
# Needs kubectl pointed at the environment's cluster (gcloud container clusters get-credentials) and kustomize.
# The image tag is the git commit the images were built from (see .github/workflows/deploy.yml).
set -euo pipefail

environment="${1:?environment (dev, staging, prod)}"
tag="${2:?image tag}"
overlay="$(cd "$(dirname "$0")/overlays/${environment}" && pwd)"
namespace=fandomscore

cd "$overlay"
# Registry prefix from the overlay's api image (IMAGE_REPOSITORY overrides it), e.g.
# us-central1-docker.pkg.dev/fandomscore-dev/fandomscore
repository="${IMAGE_REPOSITORY:-$(awk '/^  - name: api$/ { found = 1 } found && /newName:/ { print $2; exit }' kustomization.yaml | sed 's|/api$||')}"
[ -n "$repository" ] || { echo "Cannot find the image repository; set IMAGE_REPOSITORY." >&2; exit 1; }
for image in api worker migrate; do
  kustomize edit set image "${image}=${repository}/${image}:${tag}"
done

echo "== Migrations (${environment}, ${tag})"
kubectl delete job migrate -n "$namespace" --ignore-not-found --wait
# Everything except the Deployments, HPA, and PDB: namespace, identities, config, secrets, migrate job, gateway.
kustomize build . | kubectl apply -l 'fandomscore.app/stage!=app' -f -
if ! kubectl wait job/migrate -n "$namespace" --for=condition=complete --timeout=10m; then
  echo "Migration job did not complete; logs:" >&2
  kubectl logs job/migrate -n "$namespace" --all-containers >&2 || true
  exit 1
fi
kubectl logs job/migrate -n "$namespace"

echo "== Rollout"
kustomize build . | kubectl apply -f -
kubectl rollout status deployment/api -n "$namespace" --timeout=10m
kubectl rollout status deployment/worker -n "$namespace" --timeout=10m

# Leave the overlay as committed (the tag is a deploy-time value).
git -C "$overlay" checkout -- kustomization.yaml 2>/dev/null || true
echo "Deployed ${tag} to ${environment}."
