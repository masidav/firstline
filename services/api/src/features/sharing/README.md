# Score cards (sharing)

A fan shares a picture of their fandom score for an artist: "Top 3% of fans of Geese", their handle, the date, and an
"Estimated" note while the percentile is an estimate. Nothing else about the fan (no score breakdown, no email).

| Route | |
| --- | --- |
| `GET /v1/me/scores/{artistId}/card` | A signed link (one hour) to the PNG, the share sentence ("I'm in the top 3% of Geese fans on Fandom Score."), the label, and whether it's an estimate. `404` without a score or when the artist isn't active. |
| `GET /v1/score-cards/{userId}/{artistId}.png?exp=&sig=` | The 1080×1080 PNG, no sign-in (the signature is the key; the signed purpose names the artist, the id is the fan). Drawn on request from the current score. `403` for a bad or expired link. |

- Drawing: `ScoreCard.cs` with SkiaSharp (same native library as the worker's redaction). Fonts: Lato Regular and Black,
  SIL Open Font License (`fonts/OFL.txt`), embedded in the API assembly because the runtime image has no fonts.
- The app (`apps/mobile/src/features/sharing/shareCard.ts`): on a phone it downloads the PNG and opens the share sheet
  (`expo-sharing`); on the web it uses the browser's share sheet when it can take files, else downloads the image.
  The button is on the score screen ("Share my score card").

Not done: public share links with link previews (the image is shared as a file today), artist photos on the card, and
counting shares. Tests: `tests/FandomScore.Tests/ScoreCardApiTests.cs`; app `shareCard.test.ts`, `ScoreScreen.test.tsx`.
