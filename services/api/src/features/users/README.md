# User profiles

The signed-in user's own profile (`/me`), profile pictures, and account deletion. Public profiles (`/users/{handle}`)
come with the social features.

## Endpoints (openapi tag Me)
| Endpoint | Notes |
| --- | --- |
| `GET /v1/me` | `Me`: id, handle, email, display name, avatar URL, staff flag, created at. `spotify` arrives in Phase 4. |
| `PATCH /v1/me` | Changes `handle` (`409 handle_taken`) and `display_name` (1–60 characters, `null` clears). `avatar_upload_id` sets the profile picture from a finished `avatar` upload (`422` on the field otherwise); `null` removes it. `is_artist` plus the band links `band_url`, `instagram_handle`, `x_handle` (`null` clears each; a leading @ is dropped from handles). Links need `is_artist`; `is_artist: false` clears them. A missing field is left alone. |
| `DELETE /v1/me` | `202`. Sets `deleted_at`; database triggers scrub personal fields (the picture included), Spotify and library data, sessions, and sign-in identities. |
| `GET /v1/avatars/{uploadId}` | A fan's profile picture, no sign-in (image tags). Answers only while the upload is a live fan's current picture; 404 after it is changed or removed or the account deleted. Redirects to Cloud Storage where files live there. |

## Profile pictures (migration 0052, 2026-10-31)
The fan uploads a photo through the submissions upload flow (`POST /uploads` with `purpose: avatar`, JPEG or PNG up
to 15 MB). On upload, `Avatars.Process` turns it upright (EXIF orientation), crops its middle square, scales it to at
most 512 px, and re-encodes it as a JPEG, so no metadata survives; it is stored under `media/avatars/{uploadId}` (the
media bucket, not quarantine, which expires at 30 days). `PATCH /me` with the `upload_id` sets `users.avatar_upload_id`
and `users.avatar_url` (`{App:PublicOrigin}/v1/avatars/{uploadId}`); the previous picture's upload row is deleted in
the same transaction, which stops its link at once and queues its file for `storage.cleanup`. The link has no
signature because every fan who sees the name gets it and image tags can't send tokens; it is safe because the id is
unguessable and the link dies with the picture. Database rules: the picture must be a finished `avatar` upload of the
same fan, the link is cleared whenever the upload is (`users_avatar`, `users_avatar_link`), and deletion clears both.
`storage.cleanup` never treats a current picture's slot as expired.

## Profile fields
| Field | Who sees it |
| --- | --- |
| `id`, `email`, `is_staff`, `created_at` | Owner only (`/me`). |
| `handle`, `display_name`, `avatar_url` | Public (`UserRef`); null for deleted accounts. |
| `is_artist`, `band_url`, `instagram_handle`, `x_handle` | Shown on the fan's own page today; meant to be public with the artist's profile once `/users/{handle}` exists. Cleared on deletion. |

Rules shared with the database: handle `^[A-Za-z0-9_]{3,30}$`, unique ignoring case (`users_handle_format`,
`citext`); display name 1–60 characters (`users_display_name_length`); band link an https URL of at most 200 characters
(`users_band_url_check`), Instagram handle `^[A-Za-z0-9._]{1,30}$`, X handle `^[A-Za-z0-9_]{1,15}$`, and links only
while `is_artist` (`users_artist_links`, migration 0016). Deletion clears the flag and links (`users_artist_scrub`).

## Files
- `UserProfiles.cs`: `MeResponse`, handle and display-name rules, `LoadMeAsync`.
- `MeEndpoints.cs`: the endpoints above.
- `Avatars.cs`: picture processing (SkiaSharp), the storage key and link, `GET /avatars/{uploadId}`.

## Tests
`tests/FandomScore.Tests/AuthApiTests.cs` (`Profile_updates_are_validated`, `Artist_profile_links_need_the_artist_flag`,
`Deleting_the_account_ends_access_and_frees_the_identity`), `AvatarApiTests.cs` (pictures), `StorageCleanupJobTests.cs`
(a current picture's slot is kept), and the account-deletion and profile-picture sections of `database/tests/invariants.sql`.

## Known limitations
- Content is removed 30 days after deletion by the worker's `accounts.purge_deleted` job (see the privacy feature).
- A removed picture may stay in a browser's cache for up to a day (`Cache-Control: max-age=86400` on the bytes); the
  link itself answers 404 at once.
- Privacy preferences for journal and social features are not defined yet.
