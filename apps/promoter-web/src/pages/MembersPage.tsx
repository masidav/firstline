import { useState } from 'react';

import { errorMessage, type Role, unwrap } from '../api/client';
import { useSession } from '../auth/SessionProvider';
import { can, ROLE_LABELS } from '../lib/format';
import { Button, Card, ErrorText, Field, Select } from '../ui/components';
import { useOrganization } from './OrganizationLayout';

const ROLES: Role[] = ['owner', 'admin', 'analyst'];

/** Members and roles. Owners add people by handle (they need an account), change roles, and remove members. */
export function MembersPage() {
  const { organization, reloadOrganization } = useOrganization();
  const { session, state } = useSession();
  const owner = can.manageMembers(organization.my_role);
  const me = state.status === 'signedIn' ? state.me.handle : '';
  const [handle, setHandle] = useState('');
  const [role, setRole] = useState<Role>('analyst');
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string>();

  async function change(key: string, action: () => Promise<unknown>) {
    setBusy(key);
    setError(undefined);
    try {
      await action();
      await reloadOrganization();
      return true;
    } catch (e) {
      setError(errorMessage(e));
      return false;
    } finally {
      setBusy(null);
    }
  }

  const memberPath = (member: string) => ({ params: { path: { organizationId: organization.id, handle: member } } });
  const setRoleOf = (member: string, newRole: Role) =>
    change(`role-${member}`, async () => unwrap(await session.api.PUT('/organizations/{organizationId}/members/{handle}', { ...memberPath(member), body: { role: newRole } })));

  return (
    <Card title="Members">
      <p className="muted">Owners manage members. Admins run shows, allocations, and draws. Analysts can look at everything but change nothing.</p>
      <ErrorText>{error}</ErrorText>
      <table>
        <thead><tr><th>Member</th><th>Role</th><th>Added</th><th><span className="visually-hidden">Actions</span></th></tr></thead>
        <tbody>
          {organization.members.map((m) => (
            <tr key={m.handle}>
              <td>@{m.handle}{m.display_name ? ` (${m.display_name})` : ''}{m.handle === me ? ' · you' : ''}</td>
              <td>
                {owner ? (
                  <Select label={`Role of @${m.handle}`} hideLabel value={m.role} disabled={busy !== null} onChange={(e) => void setRoleOf(m.handle, e.target.value as Role)}>
                    {ROLES.map((r) => <option key={r} value={r}>{ROLE_LABELS[r]}</option>)}
                  </Select>
                ) : ROLE_LABELS[m.role]}
              </td>
              <td>{new Date(m.added_at).toLocaleDateString()}</td>
              <td>
                {owner && (
                  <Button kind="secondary" busy={busy === `remove-${m.handle}`} onClick={() => {
                    if (window.confirm(`Remove @${m.handle} from ${organization.name}?`)) {
                      void change(`remove-${m.handle}`, async () => unwrap(await session.api.DELETE('/organizations/{organizationId}/members/{handle}', memberPath(m.handle))));
                    }
                  }}>Remove</Button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {owner && (
        <form className="grid" onSubmit={(e) => {
          e.preventDefault();
          const who = handle.trim().replace(/^@/, '');
          if (!who) return setError('Enter the handle of the person to add.');
          void setRoleOf(who, role).then((ok) => ok && setHandle(''));
        }}>
          <Field label="Add a member by handle" value={handle} onChange={(e) => setHandle(e.target.value)} placeholder="their_handle" hint="They need a Fandom Score account first." />
          <Select label="Role" value={role} onChange={(e) => setRole(e.target.value as Role)}>
            {ROLES.map((r) => <option key={r} value={r}>{ROLE_LABELS[r]}</option>)}
          </Select>
          <div className="actions"><Button type="submit" busy={busy === `role-${handle.trim().replace(/^@/, '')}`}>Add member</Button></div>
        </form>
      )}
    </Card>
  );
}
