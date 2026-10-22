# ADR 0006: Album catalog from Spotify

- **Status:** Accepted (2026-10-21). Resolves the open decision "album catalog provider and its caching/artwork
  terms" (`PROJECT_TODO.md`, `ARCHITECTURE.md`). Not implemented yet: the catalog load is part of artist
  onboarding (Phase 3) and the album endpoints are Phase 6. Terms checked 2026-10-21 (sources at the end).
- **Code (planned):** `packages/spotify` (album and track lookups), `workers/artist-onboarding` (catalog load and
  refresh jobs), `services/api/src/features/album-journal` and `services/api/src/integrations/album-catalog`.
- **Contract:** `openapi.yaml`, tag Albums (`/albums/search`, `/albums/{id}`) and `/artists/{id}/albums`.

## Decision

| Concern | Choice |
| --- | --- |
| Provider | **Spotify Web API**, with the app token from ADR 0005. No second provider for now: `artists.musicbrainz_id` and `albums.musicbrainz_id` stay empty, kept for a possible later source of identifiers and links. |
| What is loaded | For each tracked artist: `GET /artists/{id}/albums` with `include_groups=album,single,compilation`, then `GET /albums/{id}/tracks` for each release. `appears_on` is excluded: those are other artists' releases, and the catalog credits the **primary (first-credited) artist only**, like the sync (ADR 0005). A release whose first-credited artist is not the tracked artist is skipped. |
| Market | One market, `Spotify__CatalogMarket` (default `US`), on both calls. Labels publish separate Spotify albums per region; one market keeps each release to one row. Change the default when the product opens a second market. |
| What is stored | Only what the app shows and the sync needs. Albums: title, type (`album`, `single`, `compilation`), release date and its precision, Spotify album id, cover art **URL**. Tracks: title, disc and track number, Spotify track id. Nothing else from the responses is kept (no label, copyrights, external ids, artist lists, markets). |
| Cover art | **Hotlinked, never copied.** We store Spotify's image URL (the largest size) and the app loads it from Spotify's CDN. Shown unaltered: no crop, overlay, filter, blur, or animation; resizing and rounded corners are fine. Shown only next to the album it belongs to. **Never** in score cards, share images, push notifications, marketing, or store listings. Missing art gets a placeholder. |
| Attribution | Every screen that shows catalog metadata or cover art carries the Spotify logo (full logo, per Spotify's branding guidelines) and links the album to Spotify (`https://open.spotify.com/album/{spotify_album_id}`). So `Album`, `AlbumRef`, and `Track` expose their Spotify ids in the contract (the grab bag's song links need them too). |
| Freshness | A `catalog.refresh` job re-runs the load for each **active** artist every `Spotify__CatalogRefreshEvery` (default 7 days), queued by an hourly scheduler like `spotify.schedule_syncs`. The load upserts by Spotify id, so titles, dates, cover URLs, and track lists follow Spotify. Running it twice gives the same result. |
| Removed releases | When Spotify stops listing an album for the artist, or answers 404 for it, the row is **kept** and `albums.delisted_at` is set. Deleting it would cascade-delete fans' logs and list entries. A delisted album leaves search, browse, and the artist's album list, and loses its cover art and Spotify link; its logs keep showing the stored title. The same applies to tracks (`tracks.delisted_at`), which `user_track_likes` depend on. |
| Activation | An artist becomes `active` only after its catalog load succeeded (an empty catalog is allowed and logged: a new artist may have nothing released in the market) and its baseline exists. Baseline parameters are a separate open decision. |
| Limits | Album pages are 10 per request (development mode), tracks 50. An artist with 200 releases costs about 220 requests, inside one job lease. `Spotify__MaxCatalogAlbums` (default 500, newest first) caps pathological catalogs. 429 and 5xx handling as in ADR 0005: short `Retry-After` waits in the job, longer ones back to the queue with backoff. |
| Popularity | `tracks.popularity` **cannot be filled**: Spotify removed `popularity` from tracks, albums, and artists in February 2026. The column stays `NULL`; the "deep cut" signal and the baseline's follower/popularity proxies are unavailable from Spotify (Phase 5 must use catalog size and our own data). |
| Co-credits | Unchanged and still open (`PROJECT_TODO.md`): primary artist only. Collaboration albums appear under whichever artist Spotify credits first. |

## Why
- **Spotify ids are already the catalog's keys.** `user_track_likes` joins Spotify track ids to `tracks`, so the
  sync can only count tracks that were loaded from Spotify. Any other provider would need an id map for every
  track; MusicBrainz's Spotify URL relationships cover only part of the releases, and newer or smaller artists
  (the ones fans add) are the least covered.
- **Already integrated.** Same client, app token, retry and 429 handling, and test fake (`FakeSpotify`) as artist
  search and the sync. The three endpoints needed survived the February 2026 changes (checked against the
  changelog and migration guide); only the batch endpoints and some fields went away.
- **The terms permit exactly this shape.** Spotify's Developer Terms forbid databases of Spotify Content except
  "as strictly necessary to operate" the app and allow temporary caching of "metadata and cover art"; they
  require data to be kept up to date and older data deleted. Storing the minimum, refreshing weekly, delisting
  removed releases, and hotlinking art (rather than copying it) is the compliant version of a catalog cache.
- **One launch blocker, not two.** The catalog shares Spotify's extended-quota requirement with the signals
  (ADR 0005). A second provider would add its own terms and rate limits without removing that blocker.

### Rejected alternatives
| Option | Why not |
| --- | --- |
| **MusicBrainz + Cover Art Archive** | Core data is CC0 and fine to store. But the Cover Art Archive states only that "all images are copyrighted by their respective copyright owners" and grants no licence, so a commercial app showing them carries the rights risk itself. The API allows 1 request per second per IP, and MetaBrainz asks companies with a public product to join a paid supporter tier (Bronze, US$100/month, and up). Coverage of small and new artists is patchy, and there is no reliable map from Spotify track ids. Kept as a possible later source of identifiers and artist links only. |
| **Apple Music API** | Apple's Apple Music API and licensing team states that cover art and metadata "may only be used in connection with music playback or playlists". A journal is neither. Apple Music returns in Phase 10 as a listening-signal source, not as the catalog. |
| **Discogs** | Its API terms page returned HTTP 403 to our checks on 2026-10-21, so the image and commercial-use rules could not be verified. No Spotify id mapping either. Not evaluated further. |
| **Copying cover art to our storage** | Would be a stored copy of Spotify Content with no need behind it ("strictly necessary" fails), and images change when labels replace artwork. Hotlinking keeps us within the temporary-cache allowance. |

## Terms we follow (checked 2026-10-21)
| Rule | Source | How the design complies |
| --- | --- | --- |
| No storing, aggregating, or databases of Spotify Content "other than as strictly necessary to operate your SDA"; "Do not store Spotify Content indefinitely." | Developer Terms IV.3.1.a | Minimal columns; weekly refresh; delisting of removed releases. |
| "Do not locally cache any Spotify Content, except as strictly necessary to enhance the performance"; metadata and cover art may be cached temporarily. | Developer Terms IV.3.2, IV.3.2.a | The catalog is a metadata cache keyed by Spotify id, refreshed on a schedule; cover art is hotlinked, not cached. |
| "Use reasonable efforts to ensure that any data you display to users is the most up to date data" and "delete older data." | Developer Terms IV.3.1.a | `catalog.refresh` every 7 days; delisted rows hidden. |
| "You may adjust the size of metadata or cover art as necessary." No modifying or creating derivative works. | Developer Terms IV.2.2.b | Resizing only; no overlays, crops, or composites. |
| "Artwork must be kept in its original form." "Don't overlay images or text on top of the artwork." "Don't crop the artwork in any way." "You may only use the artwork provided by Spotify." | Design guidelines | Same; this is why cover art stays out of score cards and share images. |
| "Metadata and cover art may only be used in connection with the underlying content." Must not be offered "as a standalone service or product." | Developer Policy II.5, II.4.3 | Art and metadata appear only on the album and artist they belong to, inside the journal. |
| "Clearly attribute the content as being supplied and made available by Spotify"; "accompanied by a link back to the applicable album ... on the Spotify Service." Metadata "must always be accompanied by the Spotify brand" and "must always link back to the Spotify Service." | Developer Policy II.4.1, II.4.2; Design guidelines | Spotify logo and album link on every catalog screen; Spotify ids in the contract. |
| No training or ingesting Spotify Content into an ML or AI model. | Developer Terms IV.2.1.a; Developer Policy III.14 | Catalog data is not used for models. |
| No transfer of Spotify Content to third parties, including derived data. | Developer Terms IV.2.5 | Catalog data is not exported or shared with promoters (Phase 11 sharing is first-party fan data with consent, not Spotify Content). |

## Risks
- **Client credentials.** Spotify has said it is "moving away from the Client Credentials flow for metadata
  endpoints" (ADR 0005). If app tokens stop working, onboarding can run with the token of the user who added the
  artist (they asked for it), but scheduled refreshes would pause until a replacement is agreed with Spotify or a
  second source exists.
- **Other services.** Developer Policy III.5 forbids products "integrated with streams or content from another
  service." Adding Apple Music signals (Phase 10) next to a Spotify catalog needs a legal read before that phase.
  The catalog itself adds no exposure beyond what the sync already has, and the same review (Phase 9, "verify
  current external API terms") must cover Policy III.13 (no derived listenership metrics or user profiles),
  which the fandom score depends on.
- **Extended quota.** Same blocker as ADR 0005: development mode allows 5 users and needs the owner's Premium.
- **Duplicates within one market.** Deluxe editions and re-releases are separate Spotify albums and will show as
  separate albums. Acceptable; fans log the edition they listened to.
- **Date precision.** Spotify gives some release dates as a year or month. The precision is stored so the app
  never shows an invented day.

## Consequences for the implementation
- **Migration** (new file, with invariant tests): `albums.album_type`, `albums.release_date_precision`,
  `albums.delisted_at`, `albums.refreshed_at`; `tracks.disc_number`, `tracks.delisted_at`; a partial index on
  live albums per artist. `artists.catalog_loaded_at` (set by the load) so activation can check it.
- **Contract:** `Album` and `AlbumRef` gain `spotify_album_id` and `album_type`, `Album` gains
  `release_date_precision`, `Track` gains `spotify_track_id` and `disc_number`. Delisted albums are excluded from
  search, browse, and `/artists/{id}/albums`; `GET /albums/{id}` still returns them (for existing logs) with
  `cover_url: null`.
- **Jobs:** `artist.onboard` (load catalog, then baseline, then activate; queued by `track_artist()` in the same
  transaction) and `catalog.refresh` with its hourly scheduler. Both idempotent.
- **Client:** `SpotifyClient.GetArtistAlbumsAsync` and `GetAlbumTracksAsync` (paged, app token), with the
  dropped fields never mapped.
- **App:** album and artist screens show the Spotify logo and link; cover art is loaded from `cover_url` with a
  placeholder; share and score-card designs use no cover art.

## Configuration (to add with the implementation)
| Variable | Meaning |
| --- | --- |
| `Spotify__CatalogMarket` | ISO 3166-1 country code used for catalog calls. Default `US`. |
| `Spotify__CatalogRefreshEvery` | How often an active artist's catalog is reloaded. Default 7 days. |
| `Spotify__MaxCatalogAlbums` | Newest releases loaded per artist. Default 500. |

## Sources checked on 2026-10-21
- Spotify Developer Terms: https://developer.spotify.com/terms (sections IV.2 and IV.3).
- Spotify Developer Policy: https://developer.spotify.com/policy (sections II.4, II.5, III).
- Spotify design guidelines (artwork, attribution, linking): https://developer.spotify.com/documentation/design
- Spotify February 2026 changelog and migration guide:
  https://developer.spotify.com/documentation/web-api/references/changes/february-2026,
  https://developer.spotify.com/documentation/web-api/tutorials/february-2026-migration-guide
- Spotify endpoint references for `GET /artists/{id}/albums` (limit max 10) and `GET /albums/{id}/tracks` (limit max 50).
- MusicBrainz data licence (core CC0, supplementary CC BY-NC-SA): https://musicbrainz.org/doc/About/Data_License;
  rate limits: https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting; supporter tiers:
  https://metabrainz.org/supporters/account-type; Cover Art Archive: https://coverartarchive.org/ and
  https://musicbrainz.org/doc/Cover_Art_Archive/API
- Apple Music API licensing statement (Apple Developer Forums, Apple Music API & Licenses team):
  https://developer.apple.com/forums/thread/681105
- Discogs API terms: https://support.discogs.com/hc/en-us/articles/360009334593 (HTTP 403 on fetch; unverified).
