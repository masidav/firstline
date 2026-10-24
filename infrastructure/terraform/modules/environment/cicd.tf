# GitHub Actions deploys through Workload Identity Federation: no long-lived keys (ADR 0002).
# Only jobs of this repository running in the GitHub environment of the same name can use the deploy account,
# so a prod deploy needs the approval configured on the prod GitHub environment.

resource "google_iam_workload_identity_pool" "github" {
  workload_identity_pool_id = "github"
  project                   = var.project_id
  display_name              = "GitHub Actions"

  depends_on = [google_project_service.apis]
}

resource "google_iam_workload_identity_pool_provider" "github" {
  workload_identity_pool_id          = google_iam_workload_identity_pool.github.workload_identity_pool_id
  workload_identity_pool_provider_id = "github-actions"
  project                            = var.project_id
  display_name                       = "GitHub Actions OIDC"

  attribute_mapping = {
    "google.subject"        = "assertion.sub"
    "attribute.repository"  = "assertion.repository"
    "attribute.environment" = "assertion.environment"
  }

  attribute_condition = "assertion.repository == \"${var.github_repository}\""

  oidc {
    issuer_uri = "https://token.actions.githubusercontent.com"
  }
}

resource "google_service_account" "deploy" {
  account_id   = "${local.name}-deploy"
  project      = var.project_id
  display_name = "GitHub Actions deployer (${var.environment})"
}

resource "google_service_account_iam_member" "deploy_federation" {
  service_account_id = google_service_account.deploy.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github.name}/attribute.environment/${var.environment}"
}

# Push images, get cluster credentials, and apply manifests in the namespace.
resource "google_artifact_registry_repository_iam_member" "deploy_push" {
  project    = var.project_id
  location   = google_artifact_registry_repository.images.location
  repository = google_artifact_registry_repository.images.name
  role       = "roles/artifactregistry.writer"
  member     = google_service_account.deploy.member
}

resource "google_project_iam_member" "deploy_cluster" {
  project = var.project_id
  role    = "roles/container.developer"
  member  = google_service_account.deploy.member
}

# deploy.sh takes an on-demand Cloud SQL backup right before the migrate Job, so a bad migration on an environment
# with real users (beta, prod) can be undone by restoring the moment before it ran. Backup permissions only.
resource "google_project_iam_custom_role" "deploy_backup" {
  role_id     = "fandomscoreDeployBackup"
  project     = var.project_id
  title       = "Fandom Score deploy backups (${var.environment})"
  description = "Create and read Cloud SQL on-demand backups before running migrations."
  permissions = [
    "cloudsql.backupRuns.create",
    "cloudsql.backupRuns.get",
    "cloudsql.backupRuns.list",
    "cloudsql.instances.get",
  ]
}

resource "google_project_iam_member" "deploy_backup" {
  project = var.project_id
  role    = google_project_iam_custom_role.deploy_backup.id
  member  = google_service_account.deploy.member
}
