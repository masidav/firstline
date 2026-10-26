# TODO — Notifications mobile UI

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [x] Request push permission only when the fan taps "Turn on push notifications" (Profile); never at launch.
- [x] Register the token on every launch once allowed (`PushSetup`, `push.ts`), and remove it on sign-out.
- [x] Preferences: per-category switches (fan tickets, presale reminders, staff notices) in Profile.
- [x] A tapped push opens its `link` (app paths only), also when it launched the app.
- [x] Revoked permission: "Open settings"; the web app explains push is for the phone app; no EAS project id: says so.
- [x] Tests: `push.test.ts` (5), `PushSettingsSection.test.tsx` (4). Verified 2026-10-26: typecheck, Jest 252/252.
- [ ] Set the EAS project id (`eas init` writes `extra.eas.projectId` to the app config) and try it on a phone with a
      development build.

## In-app notifications (2026-10-25)
- [x] Alerts button on the home screen with the unread count (polled every minute, updated when notifications are
      read), and an Alerts screen (`/notifications`): tap opens the right screen and marks it read; "Mark all read".
      Verified: NotificationsScreen and HomeScreen tests, Chrome at phone size.

## Verification
- [ ] Add or update automated tests.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the corresponding README and root project checklist.
