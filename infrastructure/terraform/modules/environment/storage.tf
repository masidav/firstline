# Submission files. Quarantine holds unredacted uploads briefly; only the redaction workload can read them.
# Its expiry rule (30 days, decided 2026-10-25) deletes every quarantined file, verified proof included, so the
# redaction workload must move proof to the archive within that window. Rejected and taken-down files are also kept
# 30 days in the app (storage.cleanup), so the rule only catches what the app missed.
# Archive holds redacted files only (schema rule: never archive unredacted files) and keeps versions.
# Media holds staff-published media (featured-artist videos); the app deletes what it no longer needs.
# The app picks the bucket from the key's prefix (quarantine/, archive/, media/; packages/storage/GcsFileStore.cs).

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

resource "google_storage_bucket" "media" {
  name                        = "${var.project_id}-media"
  project                     = var.project_id
  location                    = var.region
  storage_class               = "STANDARD"
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  force_destroy               = !var.deletion_protection
  labels                      = local.labels
}

# Access follows what each workload's code does (ADR 0002, amended 2026-10-26):
# - The API stores uploads in quarantine and reads them back for staff review and for the fan's own pending photos,
#   and deletes them when a fan withdraws or staff discard one; it reads redacted files from the archive; it stores
#   and serves featured videos in media.
# - The worker runs redaction (reads quarantine, writes the archive, deletes originals) and storage.cleanup
#   (deletes from all three).
resource "google_storage_bucket_iam_member" "api_quarantine" {
  bucket = google_storage_bucket.quarantine.name
  role   = "roles/storage.objectUser"
  member = google_service_account.workload["api"].member
}

resource "google_storage_bucket_iam_member" "api_archive_read" {
  bucket = google_storage_bucket.archive.name
  role   = "roles/storage.objectViewer"
  member = google_service_account.workload["api"].member
}

resource "google_storage_bucket_iam_member" "api_media" {
  bucket = google_storage_bucket.media.name
  role   = "roles/storage.objectUser"
  member = google_service_account.workload["api"].member
}

resource "google_storage_bucket_iam_member" "worker" {
  for_each = {
    quarantine = google_storage_bucket.quarantine.name
    archive    = google_storage_bucket.archive.name
    media      = google_storage_bucket.media.name
  }

  bucket = each.value
  role   = "roles/storage.objectUser"
  member = google_service_account.workload["worker"].member
}

# The API signs short-lived download links as itself (IAM signBlob; no key files), so its own account needs the
# token creator role on itself.
resource "google_service_account_iam_member" "api_signs_links" {
  service_account_id = google_service_account.workload["api"].name
  role               = "roles/iam.serviceAccountTokenCreator"
  member             = google_service_account.workload["api"].member
}

# A separate redaction deployment (ADR 0002) would read quarantine and write the archive under its own identity.
# Until it exists, redaction runs in the worker (workers/image-redaction) and these bindings are unused.
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
