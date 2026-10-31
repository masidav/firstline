# Storage cleanup

`StorageCleanupJob` (`storage.cleanup`, hourly) deletes stored files that no row needs, so no file outlives its rows.

## How
- Migration 0027: deleting an `uploads` or `submission_files` row that names a stored file (or changing its key)
  writes the key to `storage_deletions` in the same transaction. Withdrawn submissions, purged accounts, and every
  cascade are covered without the code that deletes them knowing about storage.
- Each run, in batches of 200 (up to 25 batches per step):
  1. Accounts deleted more than 30 days ago and not purged yet: delete their `submission_files` rows and their upload
     slots (except a featured artist's video). `purge_deleted_user()` refuses while submission files remain, so this
     is what lets `accounts.purge_deleted` purge them.
  2. Upload slots that expired unused: not attached to a submission, not used by `featured_artists`, not a fan's
     current profile picture (`users.avatar_upload_id`, migration 0052), and `SlotGrace` (1 day) past `expires_at`.
     The conditions are re-checked by the delete itself, so a slot attached meanwhile stays. A picture the fan
     changes or removes loses its row at once (`PATCH /me`), which queues its file here.
  2b. Rejected submissions (`rejected_at`, migration 0028) and taken-down photos (`removed_at`) more than 30 days
     ago: their `submission_files` rows and attached slots go; the submission row, status, and points stay. A
     submission moved back to review meanwhile keeps its files.
  3. Queued keys: if a live `uploads` or `submission_files` row still names the key, only the queue row goes (the
     live row's own delete queues it again later). Otherwise the file is deleted from storage, then the queue row.
     A file already missing is fine.
- Safe to run twice or concurrently: every step re-reads current state, and a crash leaves the key queued.

## Not done
- Orphans from before migration 0027 can't be found: `IFileStore` has no listing.
- Storage is local disk (`packages/storage`); the worker and the API must share `Storage__LocalPath` until Cloud
  Storage replaces it.

Tests: `tests/FandomScore.Tests/StorageCleanupJobTests.cs`.
