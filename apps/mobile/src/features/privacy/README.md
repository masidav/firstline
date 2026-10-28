# Privacy

"Download my data" (`GET /me/export`; what the file holds and leaves out:
`services/api/src/features/privacy/README.md`).

- `downloadMyData.ts`: fetches the export with the session's API client (it needs the sign-in, so
  `File.downloadFileAsync` can't be used), then saves it. Web: a Blob and an `<a download>`. Phone: written to the cache
  (`expo-file-system`) and handed to the share sheet (`expo-sharing`), so the fan can save it to Files, Drive, or mail.
  The file name comes from `Content-Disposition` when readable (the API exposes it to the web app), else it is built
  the same way.
- `DownloadMyDataButton.tsx`: the Profile button, with a message after a web download and a plain message for the
  hourly limit (`rate_limited`).

Tests: `downloadMyData.test.ts` (web, phone, no share sheet, errors); the button in
`../settings/ProfileScreen.test.tsx`. Not tried on a real phone yet.
