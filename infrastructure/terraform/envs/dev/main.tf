# dev environment. State lives in the GCS bucket created by ../../bootstrap.sh:
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

  environment        = "dev"
  project_id         = var.project_id
  region             = var.region
  api_domain         = var.api_domain
  billing_account_id = var.billing_account_id
  budget_monthly_usd = var.budget_monthly_usd
  alert_email        = var.alert_email
  github_repository  = var.github_repository

  sql_tier                  = "db-f1-micro"
  sql_high_availability     = false
  sql_backup_retention_days = 7
  deletion_protection       = false
}
