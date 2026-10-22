# Container images. Artifact Analysis scans every pushed image (containerscanning API).

resource "google_artifact_registry_repository" "images" {
  repository_id = "fandomscore"
  project       = var.project_id
  location      = var.region
  format        = "DOCKER"
  description   = "API, worker, and migrator images (${var.environment})"
  labels        = local.labels

  cleanup_policy_dry_run = false

  cleanup_policies {
    id     = "delete-untagged"
    action = "DELETE"
    condition {
      tag_state  = "UNTAGGED"
      older_than = "2592000s" # 30 days
    }
  }

  cleanup_policies {
    id     = "keep-recent"
    action = "KEEP"
    most_recent_versions {
      keep_count = 20
    }
  }

  depends_on = [google_project_service.apis]
}
