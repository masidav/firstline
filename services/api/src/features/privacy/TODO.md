# TODO — Privacy and account data lifecycle

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Inventory personal data, listening signals, tokens, submissions, logs, and external-provider identifiers.
  *2026-11-01: `DATA_INVENTORY.md` (every table and file type, logs, providers).*
- [ ] Define retention periods for quarantine files, archived redacted files, logs, and account data.
  *2026-11-01: proposed per data class in `DATA_INVENTORY.md` ("Retention"); housekeeping ones implemented
  (`privacy.retention`, migration 0053; archive old versions 30 days in Terraform, to apply). The rest are owner
  decisions in `PROJECT_TODO.md`.*
- [x] Document account deletion behavior for each data category and legal/product constraints. *2026-11-01:
  `DATA_INVENTORY.md` (tables, "Kept after the purge"); legal periods still owner decisions.*
- [x] Implement an authenticated deletion request and safe background cleanup workflow. (`DELETE /v1/me`; `purge_deleted_user()` run by the worker's hourly `accounts.purge_deleted` job after 30 days)
- [x] Revoke provider connections and delete encrypted tokens during disconnection/deletion as appropriate.
  *Apple revoked at deletion; Spotify has no revoke API (tokens and all synced data deleted; disconnect and account
  switch now also delete saved albums and plays, 2026-11-01).*
- [ ] Ensure deletion handles object storage, caches, search indexes, and derived data. *Derived scores and share cards done. The purge refuses accounts that still have stored submission files, so object storage must delete them first (with uploads).*
- [x] Define treatment of append-only ledger records during account deletion without violating integrity requirements. (rows kept and reversed at purge; the user row is already anonymous)
- [ ] Restrict staff access to sensitive submissions and audit access.
- [x] Add tests for access/export/deletion behavior and ensure private data is not leaked in logs.
  *Export done 2026-10-27: `GET /me/export` ("Download my data", README) with `DataExportApiTests` (own data in every
  area, no other fan's private data, no tokens/hashes in the file, rate limit). 2026-11-01: `AccountDeletionTests`
  (whole-database scan after delete and purge); logs reviewed (`DATA_INVENTORY.md`, "Logs"; outgoing HTTP request
  lines turned off).*

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
