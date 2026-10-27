-- Promoter organizations (Phase 11; decided 2026-10-27 for the pilot): Fandom Score staff create each promoter
-- organization and its first owner by hand (no self-serve sign-up). Members sign in with the same accounts fans use.
-- Roles: owner (manages members, and everything an admin does), admin (manages the organization's shows and
-- allocations and runs draws), analyst (reads them). An organization's shows are the ones it created (or staff gave
-- it); members see fans as handles and registration status only (services/api/src/features/organizations/README.md).

CREATE TABLE organizations (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name       text NOT NULL CHECK (char_length(btrim(name)) BETWEEN 1 AND 200),
  -- Artist and management organizations come later (organizations/TODO.md).
  kind       text NOT NULL DEFAULT 'promoter' CHECK (kind IN ('promoter')),
  created_by uuid REFERENCES users ON DELETE RESTRICT,                -- users are soft-deleted, never removed
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX organizations_name_uidx ON organizations (lower(btrim(name)));

CREATE TABLE organization_members (
  organization_id uuid NOT NULL REFERENCES organizations ON DELETE RESTRICT,
  user_id         uuid NOT NULL REFERENCES users ON DELETE RESTRICT,
  role            text NOT NULL CHECK (role IN ('owner', 'admin', 'analyst')),
  added_by        uuid REFERENCES users ON DELETE RESTRICT,
  added_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (organization_id, user_id)
);
CREATE INDEX organization_members_user_idx ON organization_members (user_id);

-- Shows an organization manages. Null: managed by Fandom Score staff (and every show before this migration).
ALTER TABLE events ADD COLUMN organization_id uuid REFERENCES organizations ON DELETE RESTRICT;
CREATE INDEX events_organization_idx ON events (organization_id, starts_at DESC) WHERE organization_id IS NOT NULL;

-- ---------------------------------------------------------------- audit
-- Every membership change, written by a trigger so none can be missed. The actor comes from the transaction setting
-- fandom.actor (the API sets it to the signed-in user); null for changes made by the database itself (account
-- deletion). Append-only.
CREATE TABLE organization_audit (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  organization_id uuid NOT NULL REFERENCES organizations ON DELETE RESTRICT,
  actor_id        uuid REFERENCES users ON DELETE RESTRICT,
  action          text NOT NULL CHECK (action IN ('member_added', 'role_changed', 'member_removed', 'account_deleted')),
  user_id         uuid NOT NULL REFERENCES users ON DELETE RESTRICT,
  old_role        text CHECK (old_role IN ('owner', 'admin', 'analyst')),
  new_role        text CHECK (new_role IN ('owner', 'admin', 'analyst')),
  at              timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX organization_audit_org_idx ON organization_audit (organization_id, at DESC);

CREATE FUNCTION trg_organization_audit_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'organization audit: rows are never changed or deleted' USING ERRCODE = 'P0001';
END $$;
CREATE TRIGGER trg_organization_audit_guard BEFORE UPDATE OR DELETE ON organization_audit
  FOR EACH ROW EXECUTE FUNCTION trg_organization_audit_guard();
CREATE TRIGGER trg_organization_audit_no_truncate BEFORE TRUNCATE ON organization_audit
  FOR EACH STATEMENT EXECUTE FUNCTION trg_organization_audit_guard();

-- Logs each change, and keeps an owner: removing or demoting the last owner is refused, except when the database
-- removes a deleted account's memberships (account deletion is never blocked; staff then name a new owner).
CREATE FUNCTION trg_organization_members_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_actor uuid := NULLIF(current_setting('fandom.actor', true), '')::uuid;
  v_removing_account uuid := NULLIF(current_setting('fandom.removing_account', true), '')::uuid;
  v_org uuid := COALESCE(NEW.organization_id, OLD.organization_id);
BEGIN
  IF TG_OP = 'INSERT' THEN
    INSERT INTO organization_audit (organization_id, actor_id, action, user_id, new_role)
    VALUES (NEW.organization_id, v_actor, 'member_added', NEW.user_id, NEW.role);
  ELSIF TG_OP = 'UPDATE' THEN
    IF (NEW.organization_id, NEW.user_id) IS DISTINCT FROM (OLD.organization_id, OLD.user_id) THEN
      RAISE EXCEPTION 'organization: a membership belongs to its organization and account for good' USING ERRCODE = 'P0001';
    END IF;
    IF NEW.role IS DISTINCT FROM OLD.role THEN
      INSERT INTO organization_audit (organization_id, actor_id, action, user_id, old_role, new_role)
      VALUES (NEW.organization_id, v_actor, 'role_changed', NEW.user_id, OLD.role, NEW.role);
    END IF;
  ELSE
    INSERT INTO organization_audit (organization_id, actor_id, action, user_id, old_role)
    VALUES (OLD.organization_id, v_actor,
            CASE WHEN v_removing_account = OLD.user_id THEN 'account_deleted' ELSE 'member_removed' END,
            OLD.user_id, OLD.role);
  END IF;

  IF TG_OP <> 'INSERT' AND OLD.role = 'owner' AND v_removing_account IS DISTINCT FROM OLD.user_id
     AND NOT EXISTS (SELECT 1 FROM organization_members m WHERE m.organization_id = v_org AND m.role = 'owner') THEN
    RAISE EXCEPTION 'organization: it must keep an owner' USING ERRCODE = 'P0001';
  END IF;
  RETURN NULL;
END $$;
CREATE TRIGGER trg_organization_members_change AFTER INSERT OR UPDATE OR DELETE ON organization_members
  FOR EACH ROW EXECUTE FUNCTION trg_organization_members_change();

-- Deleting an account ends its memberships (logged as account_deleted).
CREATE FUNCTION trg_user_organization_memberships_remove() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM set_config('fandom.removing_account', NEW.id::text, true);
  DELETE FROM organization_members WHERE user_id = NEW.id;
  PERFORM set_config('fandom.removing_account', '', true);
  RETURN NULL;
END $$;
CREATE TRIGGER trg_user_organization_memberships_remove AFTER UPDATE OF deleted_at ON users
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION trg_user_organization_memberships_remove();
