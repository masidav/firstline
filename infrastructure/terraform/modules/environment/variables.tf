variable "project_id" {
  description = "GCP project for this environment (one project per environment, ADR 0002)."
  type        = string
}

variable "environment" {
  description = "Cloud environment: beta (real beta testers; every push to main lands here first) or prod. Also the GitHub Actions environment allowed to deploy here. dev is not a cloud environment: it runs on the developer's machine (docs/DEVELOPMENT.md)."
  type        = string
  validation {
    condition     = contains(["beta", "prod"], var.environment)
    error_message = "environment must be beta or prod (dev runs locally)."
  }
}

variable "region" {
  description = "Region for the cluster, database, registry, and buckets."
  type        = string
  default     = "us-central1"
}

variable "api_domain" {
  description = "Public hostname of the API (e.g. api.beta.example.com). TLS is issued for it by Certificate Manager."
  type        = string
}

variable "billing_account_id" {
  description = "Billing account that pays for the project (XXXXXX-XXXXXX-XXXXXX); used for the budget alert."
  type        = string
}

variable "budget_monthly_usd" {
  description = "Monthly budget; alerts at 50 %, 90 %, 100 % of actual and 100 % of forecast spend."
  type        = number
}

variable "alert_email" {
  description = "Address that receives budget, uptime, and alerting notifications."
  type        = string
}

variable "github_repository" {
  description = "GitHub repository (owner/name) whose Actions may deploy through Workload Identity Federation."
  type        = string
  default     = "masidav/firstline"
}

variable "sql_tier" {
  description = "Cloud SQL machine tier, e.g. db-g1-small (beta), db-custom-2-7680 (prod)."
  type        = string
  default     = "db-f1-micro"
}

variable "sql_high_availability" {
  description = "Regional (high availability) Cloud SQL instance. Prod only: it roughly doubles the database cost."
  type        = bool
  default     = false
}

variable "sql_backup_retention_days" {
  description = "Daily backups kept; point-in-time recovery covers the last 7 days of transaction logs."
  type        = number
  default     = 14
}

variable "deletion_protection" {
  description = "Protect the cluster, database, and buckets from terraform destroy. Keep on for prod."
  type        = bool
  default     = true
}

variable "quarantine_retention_days" {
  description = "Days before an unredacted submission file is deleted from the quarantine bucket."
  type        = number
  default     = 30
}

variable "edge_rate_limit_per_minute" {
  description = "Cloud Armor: requests per minute per client IP before the edge returns 429."
  type        = number
  default     = 600
}

variable "secrets_directory" {
  description = "Where the pods mount Secret Manager secrets (Secrets__Directory in infrastructure/k8s/base/kustomization.yaml). The database connection string refers to the server CA file there."
  type        = string
  default     = "/secrets"
}

variable "application_secrets" {
  description = "Secret Manager secrets whose values are set out of band (gcloud secrets versions add), keyed by the configuration key the app reads (.env.example)."
  type        = set(string)
  default = [
    "Auth__SigningKey",
    "Auth__Apple__PrivateKey",
    "Spotify__ClientSecret",
    "Spotify__TokenKey",
  ]
}
