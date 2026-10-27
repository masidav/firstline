# TODO — Sharing mobile feature

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Show a preview of the card in the app before sharing (today the share sheet shows it).
- [x] The card shows artist name, top %, and the estimated label where applicable (drawn by the API).
- [x] Native share sheet on phones (expo-sharing), the browser's share sheet or a download on the web. Direct
      Instagram Stories / X flows aren't built.
- [x] Cancellation and no-share devices handled without errors.
- [x] No private details in the shared image (no email, no breakdown).
- [x] Tests: shareCard.test.ts (phone, no sharing, web share and cancel, web download), ScoreScreen share button.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
