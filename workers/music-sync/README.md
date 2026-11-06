# Apple Music and Tidal sync worker

| File | What |
| --- | --- |
| `MusicSyncJob.cs` | `music.sync` (`{user_id, provider}`): refreshes (Tidal) or uses (Apple Music) the stored token, reads the library, matches it to the catalog, replaces the provider's rows in `user_artist_signals`, `user_track_likes`, `user_album_saves`, learns ISRCs and UPCs, queues rescores. Failure states as the Spotify sync. |
| `MusicMatcher.cs` | Provider items → tracked artists: artist by normalized name (ambiguous names skipped), track by ISRC then title (album title breaks ties), album by UPC then title. `Normalize()` is the shared rule. |
| `ScheduleMusicSyncsJob.cs` | Hourly: queues connections of configured providers whose last attempt is older than `MusicProviders__SyncEvery`. |

What each provider can and can't give, and how the score reads rows from several providers, is in
[ADR 0007](../../docs/adr/0007-music-providers.md). Tests: `tests/FandomScore.Tests/MusicSyncJobTests.cs`.
