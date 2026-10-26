# One Google service account per workload, bound to the Kubernetes service account of the same name
# through Workload Identity Federation for GKE. No key files anywhere.

resource "google_service_account" "workload" {
  for_each = toset(local.workloads)

  account_id   = "${local.name}-${each.value}"
  project      = var.project_id
  display_name = "${each.value} workload (${var.environment})"
}

resource "google_service_account_iam_member" "workload_identity" {
  for_each = toset(local.workloads)

  service_account_id = google_service_account.workload[each.value].name
  role               = "roles/iam.workloadIdentityUser"
  member             = "serviceAccount:${var.project_id}.svc.id.goog[${local.namespace}/${each.value}]"

  # The project's workload identity pool (<project>.svc.id.goog) only exists once the first cluster that uses
  # Workload Identity has been created; binding to it before that fails with "Identity Pool does not exist".
  depends_on = [google_container_cluster.main]
}
