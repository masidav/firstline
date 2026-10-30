# Promoter organizations

Promoters manage their own shows and fan allocations through an organization (migration 0037). Decided 2026-10-27
for the pilot:
- **Fandom Score staff create each organization** and name its first owner (`POST /v1/admin/organizations`); there is
  no self-serve sign-up.
- **Members sign in with the accounts fans use** (Google or Apple); an account can belong to several organizations.
- **Promoters see fans as handles and registration status only**: no scores, hub cities, or contact details. Sharing
  fan data with promoters needs the fan's consent (PROJECT_TODO.md Phase 11).

## Roles
| Role | Can |
| --- | --- |
| `owner` | Everything an admin can, and add members, change roles, remove members. |
| `admin` | Create and edit the organization's shows and allocations, publish, cancel, delete drafts, queue the draw. |
| `analyst` | Read the organization, its shows and allocations, registrations, the draw, and who holds seats. |

An organization always keeps an owner (the database refuses removing or demoting the last one), except that deleting
an account always goes through; staff then name a new owner.

## Endpoints
- Staff: `POST`/`GET /v1/admin/organizations`. Staff can also create a show for an organization
  (`organization_id` on `POST /v1/admin/events`).
- Members: `GET /v1/me/organizations`, `GET /v1/organizations/{id}`; owners `PUT`/`DELETE /v1/organizations/{id}/members/{handle}`.
- Shows and allocations under `/v1/organizations/{id}`: the same handlers as staff use under `/v1/admin`
  (`AllocationEndpoints.Organizations.cs`), behind `OrganizationAccess`: the caller must be a member with the role,
  and the show or allocation in the route must be the organization's. Non-members get `404`, as if the organization
  didn't exist; members without the role get `403`. Registrations come back as `PromoterRegistration`.
- Case-study report: `GET /v1/organizations/{id}/events/{eventId}/report` (any member) and
  `PUT /v1/organizations/{id}/allocations/{allocationId}/complaints` (admins; hand-entered scalping complaints,
  migration 0047). See `services/api/src/features/allocations/README.md`.

## Data
`organizations`, `organization_members` (role per member), `events.organization_id` (null: staff-managed), and
`organization_audit`: every membership change, written by a trigger with the actor from the transaction setting
`fandom.actor` (the API sets it), append-only.

## Not yet
The promoter web dashboard on top of these endpoints, artist and management organizations, and fan data sharing with
consent. Tests: `OrganizationApiTests`, invariants (0037).
