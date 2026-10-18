-- Phase 2: sessions can end because the sign-in provider withdrew consent
-- (Sign in with Apple "consent-revoked" notification). docs/adr/0003-authentication.md.
ALTER TABLE auth_sessions DROP CONSTRAINT auth_sessions_revoked_reason_check;
ALTER TABLE auth_sessions ADD CONSTRAINT auth_sessions_revoked_reason_check
  CHECK (revoked_reason IN ('sign_out', 'token_reuse', 'provider_revoked'));
