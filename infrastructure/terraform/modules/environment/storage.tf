# Submission files. Quarantine holds unredacted uploads briefly; the redaction workload and the API (staff review,
# a fan's own pending photo) can read them, the general worker can't.
# Its expiry rule (30 days, decided 2026-10-25) deletes every quarantined file, verified proof included, so the
# redaction workload must move proof to the archive within that window. Rejected and taken-down files are also kept
# 30 days in the app (storage.cleanup), so the rule only catches what the app missed.
# Archive holds redacted files only (schema rule: never archive unredacted files) and keeps versions.
# Media holds staff-published media (featured-artist videos) and fans' profile pictures (media/avatars/, migration
# 0052); the app deletes what it no longer needs.
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

# Access follows what each workload's code does (ADR 0002, amended 2026-10-26 and 2026-10-27):
# - The API stores uploads in quarantine and reads them back for staff review and for the fan's own pending photos,
#   and deletes them when a fan withdraws or staff discard one; it reads redacted files from the archive; it stores
#   and serves featured videos in media.
# - The redaction deployment (worker image with Jobs__OnlyKinds, infrastructure/k8s/base/redaction-deployment.yaml)
#   runs submissions.redact (reads quarantine, writes the archive, deletes a half-written archive file),
#   redaction.sweep, and storage.cleanup (deletes from all three buckets), so it reads, writes, and deletes in all
#   three. objectUser rather than objectCreator on the archive: a retried redaction overwrites its own file, which
#   needs delete.
# - The general worker (Spotify, Ticketmaster, push, scoring, ...) touches no bucket and gets no access.
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

resource "google_storage_bucket_iam_member" "redaction" {
  for_each = {
    quarantine = google_storage_bucket.quarantine.name
    archive    = google_storage_bucket.archive.name
    media      = google_storage_bucket.media.name
  }

  bucket = each.value
  role   = "roles/storage.objectUser"
  member = google_service_account.workload["redaction"].member
}

# The API signs short-lived download links as itself (IAM signBlob; no key files), so its own account needs the
# token creator role on itself.
resource "google_service_account_iam_member" "api_signs_links" {
  service_account_id = google_service_account.workload["api"].name
  role               = "roles/iam.serviceAccountTokenCreator"
  member             = google_service_account.workload["api"].member
}
