# Application secrets live in Secret Manager; pods mount them as files through the GKE add-on.
# Values are added out of band and never appear in Terraform state (except the database connection string).

resource "google_secret_manager_secret" "app" {
  for_each = var.application_secrets

  secret_id = each.value
  project   = var.project_id
  labels    = local.labels

  replication {
    auto {}
  }

  depends_on = [google_project_service.apis]
}

locals {
  secret_ids = merge(
    { for key, secret in google_secret_manager_secret.app : key => secret.secret_id },
    { (google_secret_manager_secret.db.secret_id) = google_secret_manager_secret.db.secret_id },
    { (google_secret_manager_secret.db_ca.secret_id) = google_secret_manager_secret.db_ca.secret_id },
  )
}

resource "google_secret_manager_secret_iam_member" "readers" {
  for_each = local.secret_bindings

  project   = var.project_id
  secret_id = local.secret_ids[each.value.secret]
  role      = "roles/secretmanager.secretAccessor"
  member    = google_service_account.workload[each.value.workload].member
}
