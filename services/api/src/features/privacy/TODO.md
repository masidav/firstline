# TODO — Privacy and account data lifecycle

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Inventory personal data, listening signals, tokens, submissions, logs, and external-provider identifiers.
- [ ] Define retention periods for quarantine files, archived redacted files, logs, and account data.
- [ ] Document account deletion behavior for each data category and legal/product constraints.
- [x] Implement an authenticated deletion request and safe background cleanup workflow. (`DELETE /v1/me`; `purge_deleted_user()` run by the worker's hourly `accounts.purge_deleted` job after 30 days)
- [ ] Revoke provider connections and delete encrypted tokens during disconnection/deletion as appropriate.
- [ ] Ensure deletion handles object storage, caches, search indexes, and derived data. *Derived scores and share cards done. The purge refuses accounts that still have stored submission files, so object storage must delete them first (with uploads).*
- [x] Define treatment of append-only ledger records during account deletion without violating integrity requirements. (rows kept and reversed at purge; the user row is already anonymous)
- [ ] Restrict staff access to sensitive submissions and audit access.
- [ ] Add tests for access/export/deletion behavior and ensure private data is not leaked in logs.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
