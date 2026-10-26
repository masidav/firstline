# TODO — Authentication mobile UI

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [x] Build sign-in and registration screens for the selected authentication method. *2026-10-26: `SignInScreen`
      with Continue with Google (web redirect; native auth session with PKCE), Continue with Apple (iOS), and the
      developer sign-in; `FinishSignIn` asks a new account for a handle.*
- [x] Validate form inputs and display accessible error messages. *Handle errors on the field; sign-in failures as
      an alert with a way back.*
- [x] Handle loading, cancellation, network failures, and expired sessions. *Busy buttons; backing out on the
      provider leaves the screen as it was; an expired identity token says so; sessions: `SessionManager`.*
- [x] Store session material using the project's approved secure storage approach. *Refresh token and provider in
      the Keychain (native) or localStorage (web, known limit).*
- [x] Implement logout and clear user-specific cached state. *`session.signOut()` on the profile screen.*
- [x] Add screen tests for success, validation, cancellation, and session expiry. *SignInScreen 9 tests,
      providerSignIn 9, googleSignIn.native 5, appleSignIn 4, session 8 (2026-10-26).*
- [ ] Run Sign in with Apple and native Google sign-in on a device (needs the Apple Developer Program).

## Verification
- [x] Add or update automated tests. *See above.*
- [x] Run the relevant tests and record actual results. *2026-10-26: `npm test` 37 suites, 172 tests passed;
      Chrome smoke test of the web Google flow against a dev API with a stubbed Google page.*
- [x] Update the corresponding README and root project checklist. *`apps/mobile/README.md` "Sign in with Google"
      and "Sign in with Apple"; `apps/mobile/TODO.md`.*
