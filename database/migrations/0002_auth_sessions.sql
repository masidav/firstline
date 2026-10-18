-- Phase 2: sign-in identities, sessions, and rotating refresh tokens (openapi Auth tag).
-- Design: docs/adr/0003-authentication.md.

-- Handle and display name rules match openapi (SessionRequest.handle, MeUpdate).
ALTER TABLE users
  ADD CONSTRAINT users_handle_format CHECK (handle ~ '^[A-Za-z0-9_]{3,30}$'),
  ADD CONSTRAINT users_display_name_length CHECK (char_length(display_name) BETWEEN 1 AND 60);

CREATE TYPE auth_provider AS ENUM ('apple', 'google');

-- A provider account (Apple or Google `sub`) that signs in as a user. No tokens or emails are kept here.
CREATE TABLE auth_identities (
  provider     auth_provider NOT NULL,
  subject      text NOT NULL,
  user_id      uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  created_at   timestamptz NOT NULL DEFAULT now(),
  last_used_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (provider, subject)
);
CREATE INDEX auth_identities_user_idx ON auth_identities (user_id);

-- One signed-in device. Access tokens carry the session id, so revoking the session ends access at once.
CREATE TABLE auth_sessions (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id           uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  created_at        timestamptz NOT NULL DEFAULT now(),
  last_refreshed_at timestamptz NOT NULL DEFAULT now(),
  expires_at        timestamptz NOT NULL,          -- absolute lifetime; refreshing never extends it
  revoked_at        timestamptz,
  revoked_reason    text CHECK (revoked_reason IN ('sign_out', 'token_reuse')),
  CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL)),
  CHECK (expires_at > created_at)
);
CREATE INDEX auth_sessions_user_idx ON auth_sessions (user_id);

-- Refresh tokens are opaque random values; only their SHA-256 is stored. Each use rotates the token.
-- Presenting a token that was already used means it leaked, so the whole session is revoked.
CREATE TABLE refresh_tokens (
  token_hash bytea PRIMARY KEY CHECK (octet_length(token_hash) = 32),
  session_id uuid NOT NULL REFERENCES auth_sessions ON DELETE CASCADE,
  issued_at  timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  used_at    timestamptz,
  CHECK (expires_at > issued_at)
);
-- At most one usable token per session.
CREATE UNIQUE INDEX refresh_tokens_current_uidx ON refresh_tokens (session_id) WHERE used_at IS NULL;

-- Revocation is permanent, and used tokens stay used.
CREATE FUNCTION trg_auth_session_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.revoked_at IS NOT NULL AND (NEW.revoked_at, NEW.revoked_reason)
     IS DISTINCT FROM (OLD.revoked_at, OLD.revoked_reason) THEN
    RAISE EXCEPTION 'session % is revoked; revocation is permanent', OLD.id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER auth_sessions_guard BEFORE UPDATE ON auth_sessions
  FOR EACH ROW EXECUTE FUNCTION trg_auth_session_guard();

CREATE FUNCTION trg_refresh_token_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.used_at IS NOT NULL AND NEW.used_at IS DISTINCT FROM OLD.used_at THEN
    RAISE EXCEPTION 'refresh token was already used';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER refresh_tokens_guard BEFORE UPDATE ON refresh_tokens
  FOR EACH ROW EXECUTE FUNCTION trg_refresh_token_guard();

-- Deleted accounts cannot sign in or gain new sessions (guards a sign-in racing a deletion).
CREATE FUNCTION trg_require_live_user() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM users WHERE id = NEW.user_id AND deleted_at IS NOT NULL) THEN
    RAISE EXCEPTION 'user % is deleted', NEW.user_id;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER auth_identities_live_user BEFORE INSERT OR UPDATE OF user_id ON auth_identities
  FOR EACH ROW EXECUTE FUNCTION trg_require_live_user();
CREATE TRIGGER auth_sessions_live_user BEFORE INSERT OR UPDATE OF user_id ON auth_sessions
  FOR EACH ROW EXECUTE FUNCTION trg_require_live_user();

-- Account deletion ends every session and frees the provider identities, so the same Apple or Google
-- account can create a new account later (as the handle and email can).
CREATE FUNCTION trg_user_delete_auth() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  DELETE FROM auth_sessions   WHERE user_id = NEW.id;
  DELETE FROM auth_identities WHERE user_id = NEW.id;
  RETURN NULL;
END $$;
CREATE TRIGGER users_delete_auth AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_delete_auth();
