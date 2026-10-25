# TODO — Notifications mobile UI

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [ ] Request push permissions at a clear, user-appropriate moment.
- [ ] Register device tokens through the API and handle token refresh.
- [ ] Implement notification preferences and allow users to opt out.
- [ ] Route supported notification taps to the correct screen.
- [ ] Handle revoked permissions and invalid tokens gracefully.
- [ ] Add tests for preferences, navigation, and permission denial.

## In-app notifications (2026-10-25)
- [x] Alerts button on the home screen with the unread count (polled every minute, updated when notifications are
      read), and an Alerts screen (`/notifications`): tap opens the right screen and marks it read; "Mark all read".
      Verified: NotificationsScreen and HomeScreen tests, Chrome at phone size.

## Verification
- [ ] Add or update automated tests.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the corresponding README and root project checklist.
