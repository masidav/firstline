# User profiles

The signed-in user's own profile (`/me`) and account deletion. Public profiles (`/users/{handle}`) come with
the social features.

## Endpoints (openapi tag Me)
| Endpoint | Notes |
| --- | --- |
| `GET /v1/me` | `Me`: id, handle, email, display name, avatar URL, staff flag, created at. `spotify` arrives in Phase 4. |
| `PATCH /v1/me` | Changes `handle` (`409 handle_taken`) and `display_name` (1–60 characters, `null` clears). `avatar_upload_id: null` removes the avatar; setting one waits for uploads. A missing field is left alone. |
| `DELETE /v1/me` | `202`. Sets `deleted_at`; database triggers scrub personal fields, Spotify and library data, sessions, and sign-in identities. |

## Profile fields
| Field | Who sees it |
| --- | --- |
| `id`, `email`, `is_staff`, `created_at` | Owner only (`/me`). |
| `handle`, `display_name`, `avatar_url` | Public (`UserRef`); null for deleted accounts. |

Rules shared with the database: handle `^[A-Za-z0-9_]{3,30}$`, unique ignoring case (`users_handle_format`,
`citext`); display name 1–60 characters (`users_display_name_length`).

## Files
- `UserProfiles.cs`: `MeResponse`, handle and display-name rules, `LoadMeAsync`.
- `MeEndpoints.cs`: the endpoints above.

## Tests
`tests/FandomScore.Tests/AuthApiTests.cs` (`Profile_updates_are_validated`,
`Deleting_the_account_ends_access_and_frees_the_identity`) and the account-deletion section of
`database/tests/invariants.sql`.

## Known limitations
- Content removal after deletion (logs, comments, lists, submissions) waits on the retention decision.
- Avatar uploads need `POST /uploads` (object storage).
- Privacy preferences for journal and social features are not defined yet.
