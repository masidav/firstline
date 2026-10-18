# TODO — Promoter web dashboard

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Choose the web stack (proposed: React + TypeScript, sharing the client generated from `openapi.yaml`).
- [ ] Organization sign-in and member management.
- [ ] Create and edit events and allocation pools; preview eligible-fan counts before opening registration.
- [ ] Monitor registration and selection in near real time; trigger or schedule the draw.
- [ ] Export reports and consented fan data; download the case-study metrics.
- [ ] Accessibility (WCAG 2.2 AA) and responsive layout.
- [ ] Component tests (Vitest) and end-to-end tests (Playwright).

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
