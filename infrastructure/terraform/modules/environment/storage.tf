# Submission files. Quarantine holds unredacted uploads briefly; only the redaction workload can read them.
# Archive holds redacted files only (schema rule: never archive unredacted files) and keeps versions.

resource "google_storage_bucket" "quarantine" {
  name                        = "${var.project_id}-quarantine"
  project                     = var.project_id
  location                    = var.region
  storage_class               = "STANDARD"
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  force_destroy               = !var.deletion_protection
  labels                      = local.labels

  lifecycle_rule {
    action {
      type = "Delete"
    }
    condition {
      age = var.quarantine_retention_days
    }
  }
}

resource "google_storage_bucket" "archive" {
  name                        = "${var.project_id}-archive"
  project                     = var.project_id
  location                    = var.region
  storage_class               = "STANDARD"
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  force_destroy               = !var.deletion_protection
  labels                      = local.labels

  versioning {
    enabled = true
  }
}

# The API only writes uploads into quarantine and reads redacted files from the archive.
resource "google_storage_bucket_iam_member" "api_quarantine_write" {
  bucket = google_storage_bucket.quarantine.name
  role   = "roles/storage.objectCreator"
  member = google_service_account.workload["api"].member
}

resource "google_storage_bucket_iam_member" "api_archive_read" {
  bucket = google_storage_bucket.archive.name
  role   = "roles/storage.objectViewer"
  member = google_service_account.workload["api"].member
}

# Redaction reads and deletes quarantined files and writes redacted ones to the archive.
resource "google_storage_bucket_iam_member" "redaction_quarantine" {
  bucket = google_storage_bucket.quarantine.name
  role   = "roles/storage.objectAdmin"
  member = google_service_account.workload["redaction"].member
}

resource "google_storage_bucket_iam_member" "redaction_archive_write" {
  bucket = google_storage_bucket.archive.name
  role   = "roles/storage.objectCreator"
  member = google_service_account.workload["redaction"].member
}
