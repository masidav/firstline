# TODO — Mobile application

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Inspect current Expo/React Native setup, navigation, state management, styling, and build scripts.
- [ ] Establish shared UI patterns, theme tokens, loading states, empty states, and error states.
- [ ] Set up typed API client and centralized session handling using the project's existing language conventions.
- [ ] Add navigation and screens for the features in the product specification.
- [ ] Implement secure handling of authentication state and avoid storing secrets in plain text.
- [ ] Support offline album-journal writes and a retryable synchronization queue.
- [ ] Add accessibility labels, keyboard handling, and responsive layouts for mobile devices.
- [ ] Handle API errors, network loss, expired sessions, and background/resume behavior.
- [ ] Add component and screen tests using the existing test stack.
- [ ] Verify iOS and Android builds and document required configuration.

## Ticket allocations (Phase 11)
- [ ] Show upcoming allocations for followed artists and the fan's eligibility, with what to do to qualify.
- [ ] Register for an allocation; show status (registered, selected, not selected, expired).
- [ ] Claim a reservation and hand off to checkout; show the non-transferable ticket or delivery date.
- [ ] Face-value exchange or return flow.
- [ ] Release builds via EAS Build; beta via TestFlight and Google Play closed testing.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
