# TODO — Object storage integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Choose an S3-compatible provider and configure separate quarantine and archive locations.
- [ ] Keep uploaded objects private and use short-lived signed URLs where needed.
- [ ] Validate object keys and prevent path traversal or user-controlled bucket selection.
- [ ] Implement upload, download, move, and delete operations with least-privilege permissions.
- [ ] Require redacted=true before an object can enter archive storage.
- [ ] Record object hashes and storage metadata without logging sensitive contents.
- [ ] Define retention and lifecycle policies for quarantine and archive objects.
- [ ] Add tests for authorization, failed moves, archive guards, and deletion.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
