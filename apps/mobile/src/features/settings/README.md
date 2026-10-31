# Settings

## Purpose
Defines the scope and responsibilities for settings in Fandom Score.

## What exists
`ProfileScreen.tsx` (`/profile`): the profile picture (`avatar.ts`: pick a photo with the system's square crop, upload
it as `purpose: avatar`, `PATCH /me avatar_upload_id`; Remove sends null; shown by `ui/Avatar`, which also sits on the
Profile tab and next to every fan's name), handle and display name, the "I'm an artist" switch with the band's profile
link, Instagram, and X (`PATCH /me`), Spotify and hub-city entry points, "Download my data" (`../privacy`), sign out, and
account deletion. When the fan is an artist the band links show at the top of the page and open in the browser
(`bandLinks`). Handles are stored bare; the screen builds the instagram.com and x.com links. Tests: `ProfileScreen.test.tsx`.

## How to work here
- Inspect existing code and conventions before changes.
- Follow the actionable checklist in `TODO.md`.
- Preserve existing working functionality.
- Add tests and document actual verification results.
- Record unresolved decisions rather than assuming answers.

## Dependencies
Refer to the root `ARCHITECTURE.md` and `PROJECT_TODO.md`, plus the feature checklist for implementation order and interfaces.
