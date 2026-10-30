# TODO — Settings

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [x] Provide account and profile settings supported by the backend. (handle, display name, artist flag and band
  links; verified 2026-10-24: `npm test` ProfileScreen 5/5, `npm run typecheck`)
- [ ] Provide Spotify connection/disconnection controls.
- [ ] Provide privacy and notification preferences.
- [x] Provide account deletion entry point and explain its consequences.
- [ ] Show app version and support/privacy links when available.
- [x] Add tests for settings persistence and authorization. (2026-10-30, `ProfileScreen.test.tsx`: Save off until a
      change; handle and display name sent as a `PATCH /me` of only the changed fields and the answer stored in the
      session; a cleared display name saved as none; a taken handle, an unreachable server, and a failed switch
      explained without marking anything saved. Authorization: the `/profile` route waits for the session, sends a
      signed-out fan to the start, and shows the signed-in fan; staff-only entries show for staff only; an ended
      session (401 `unauthorized`) is explained instead of saved; Sign out forgets the device for push first.
      The tests found a taken handle shown twice (field and form); fixed.)

## Verification
- [x] Add or update automated tests. (`ProfileScreen.test.tsx` 19 tests.)
- [x] Run the relevant tests and record actual results. 2026-10-30: `npm test` 51 suites, 327 tests passed;
      `npm run typecheck` clean.
- [ ] Update the corresponding README and root project checklist.
