# TODO — Sharing and score cards API

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Define the score-card payload and ensure it contains only shareable information (artist, top %, handle, date,
      estimate note).
- [x] Generate score-card images: drawn by the API with SkiaSharp (README.md).
- [x] Score cards reflect the current score and the estimated label (drawn on request).
- [x] Signed, expiring image links (one hour); the image is shared as a file, so no public page yet.
- [ ] Public share pages with link previews (Open Graph), and whether fans can revoke them.
- [ ] Track share-card creation and relevant outbound activity without logging unnecessary personal data.
- [x] Tests for payload, link access (tampered, other artist, paused artist), and estimated labels (ScoreCardApiTests).

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
