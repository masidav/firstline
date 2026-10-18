# TODO — Privacy and account data lifecycle

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Inventory personal data, listening signals, tokens, submissions, logs, and external-provider identifiers.
- [ ] Define retention periods for quarantine files, archived redacted files, logs, and account data.
- [ ] Document account deletion behavior for each data category and legal/product constraints.
- [ ] Implement an authenticated deletion request and safe background cleanup workflow.
- [ ] Revoke provider connections and delete encrypted tokens during disconnection/deletion as appropriate.
- [ ] Ensure deletion handles object storage, caches, search indexes, and derived data.
- [ ] Define treatment of append-only ledger records during account deletion without violating integrity requirements.
- [ ] Restrict staff access to sensitive submissions and audit access.
- [ ] Add tests for access/export/deletion behavior and ensure private data is not leaked in logs.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
