#!/usr/bin/env bash
# One-time setup of a GCP project for an environment, before the first `terraform apply`:
# links billing, enables the APIs Terraform itself needs, and creates the state bucket.
#
#   ./bootstrap.sh <project-id> <billing-account-id> [region]
#
# The project must already exist (gcloud projects create <project-id>), and you need Owner on it.
set -euo pipefail

project="${1:?project id}"
billing="${2:?billing account id, e.g. XXXXXX-XXXXXX-XXXXXX}"
region="${3:-us-central1}"
bucket="${project}-tfstate"

gcloud billing projects link "$project" --billing-account "$billing"
gcloud services enable --project "$project" \
  cloudresourcemanager.googleapis.com serviceusage.googleapis.com iam.googleapis.com \
  storage.googleapis.com cloudbilling.googleapis.com billingbudgets.googleapis.com

if ! gcloud storage buckets describe "gs://${bucket}" --project "$project" >/dev/null 2>&1; then
  gcloud storage buckets create "gs://${bucket}" --project "$project" --location "$region" \
    --uniform-bucket-level-access --public-access-prevention
  gcloud storage buckets update "gs://${bucket}" --versioning
fi

echo "State bucket: gs://${bucket}"
echo "Next: cd envs/<env> && terraform init -backend-config=\"bucket=${bucket}\""
