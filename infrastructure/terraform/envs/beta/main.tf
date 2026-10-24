# beta environment: real beta testers, smaller than prod. Every push to main deploys here first (after CI), with a
# Cloud SQL backup before migrations; prod is promoted from it after approval. State lives in the GCS bucket created
# by ../../bootstrap.sh:
#   terraform init -backend-config="bucket=${PROJECT_ID}-tfstate"
terraform {
  required_version = ">= 1.9"

  backend "gcs" {
    prefix = "environment"
  }

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.30"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "google" {
  project               = var.project_id
  region                = var.region
  user_project_override = true
  billing_project       = var.project_id
}

module "environment" {
  source = "../../modules/environment"

  environment        = "beta"
  project_id         = var.project_id
  region             = var.region
  api_domain         = var.api_domain
  billing_account_id = var.billing_account_id
  budget_monthly_usd = var.budget_monthly_usd
  alert_email        = var.alert_email
  github_repository  = var.github_repository

  sql_tier                  = "db-g1-small"
  sql_high_availability     = false
  sql_backup_retention_days = 14
  deletion_protection       = true
}
