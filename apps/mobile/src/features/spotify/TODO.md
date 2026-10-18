# TODO — Spotify mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Build connect/disconnect Spotify UI using the API's OAuth flow.
- [ ] Explain the requested permissions and handle cancellation or denial.
- [ ] Show connection state and last successful sync time.
- [ ] Provide a manual sync action if supported and prevent duplicate requests.
- [ ] Show sync progress and recoverable error states.
- [ ] Handle reauthorization when refresh fails or scopes are missing.
- [ ] Add tests for connect, cancel, disconnect, sync, and error states.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
