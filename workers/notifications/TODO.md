# TODO — Notification worker

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Consume notification jobs and validate that the user/device remains eligible (deleted accounts, disabled devices).
- [x] Check user preferences immediately before sending (`push_tickets`, `push_presales`, `push_staff`).
- [x] Deduplicate by notification and device (`push_deliveries` primary key; the job's dedupe key).
- [x] Send through Expo's push service with safe retry behavior (429 and 5xx retry through the queue).
- [x] Deactivate invalid device tokens (`DeviceNotRegistered` at send or receipt) and record delivery outcomes.
- [ ] Rate-limit noncritical notifications (none are noisy yet). Lock-screen text has no ticket tokens or personal data.
- [x] Tests: `PushNotificationTests.cs` (5: registration, sending once per device, settings, stale and read
      notifications, unregistered devices, receipts, account deletion), invariants for 0032. Verified 2026-10-26.
- [ ] Try it on a real phone (needs an EAS project id and a development build; Expo Go can't receive pushes on Android).

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
