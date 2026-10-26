namespace FandomScore.Spotify;

/// <summary>
/// Configuration section <c>Spotify</c>, read by the API and the worker. The token key and client secret are
/// secrets and come from the environment (<c>Spotify__TokenKey</c>, <c>Spotify__ClientSecret</c>).
/// See docs/adr/0005-spotify-integration.md.
/// </summary>
public sealed class SpotifyOptions
{
    public const string Section = "Spotify";

    /// <summary>
    /// Scopes the app must request. Spotify grants all or none, so a connection missing one was made with an
    /// outdated app and must reconnect.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredScopes =
    [
        "user-library-read",           // saved tracks
        "user-top-read",               // top artists
        "user-read-recently-played",   // recent plays
        "playlist-read-private",       // the user's private playlists
        "playlist-read-collaborative", // playlists they collaborate on
        FollowScope,                   // artists they follow
        PlaybackReadScope,             // what's playing, and on which device
        PlaybackControlScope,          // play, pause, skip when the fan presses a button
    ];

    /// <summary>
    /// Scope for followed artists, added 2026-10-24. Connections made before it was required lack it: the sync
    /// then skips follows and the app asks the fan to connect again.
    /// </summary>
    public const string FollowScope = "user-follow-read";

    /// <summary>
    /// Scopes for the app's now-playing bar and play buttons, added 2026-10-26. Connections made before lack them:
    /// the player reports <c>reconnect_needed</c> and the app asks the fan to connect again.
    /// </summary>
    public const string PlaybackReadScope = "user-read-playback-state";

    /// <inheritdoc cref="PlaybackReadScope"/>
    public const string PlaybackControlScope = "user-modify-playback-state";

    /// <summary>Client ID of the app in the Spotify developer dashboard. Not a secret. Empty disables Spotify.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// Client secret from the Spotify dashboard. Secret. Used only for app tokens (client credentials) for artist
    /// search and lookup; user connections use PKCE without it. Empty turns artist search and add off.
    /// </summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Comma-separated redirect URIs the mobile app may use. Each must also be registered in the Spotify dashboard.
    /// </summary>
    public string RedirectUris { get; set; } = "";

    /// <summary>Base64 of 32 random bytes. Encrypts stored refresh tokens (AES-256-GCM).</summary>
    public string TokenKey { get; set; } = "";

    /// <summary>
    /// Comma-separated earlier token keys, still accepted for decryption. Each sync re-encrypts with
    /// <see cref="TokenKey"/>, so a retired key can be dropped once every connection has synced.
    /// </summary>
    public string PreviousTokenKeys { get; set; } = "";

    /// <summary>Automatic sync interval per connection.</summary>
    public TimeSpan SyncEvery { get; set; } = TimeSpan.FromDays(1);

    /// <summary>openapi.yaml promises one manual sync per 15 minutes per user.</summary>
    public TimeSpan ManualSyncInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many of the fan's Spotify top artists a sync adds to the tracked list when they are not tracked yet
    /// (they onboard like a manual add, then score on the fan's next sync). 0 turns this off. Needs
    /// <see cref="ClientSecret"/>, like adding by hand.
    /// </summary>
    public int AutoTrackTopArtists { get; set; } = 20;

    /// <summary>Saved tracks read per sync (50 per request).</summary>
    public int MaxSavedTracks { get; set; } = 5000;

    /// <summary>Playlists read per sync. Only playlists the user owns or collaborates on are read.</summary>
    public int MaxPlaylists { get; set; } = 50;

    /// <summary>Tracks read per playlist.</summary>
    public int MaxPlaylistItems { get; set; } = 500;

    /// <summary>Saved albums read per sync (50 per request).</summary>
    public int MaxSavedAlbums { get; set; } = 1000;

    /// <summary>Followed artists read per sync (50 per request).</summary>
    public int MaxFollowedArtists { get; set; } = 1000;

    /// <summary>
    /// How often the play ledger is extended from Spotify's last-50-plays endpoint (job spotify.poll_plays).
    /// Spotify keeps only the last 50 plays, so a heavy listener needs frequent polls to be counted in full.
    /// </summary>
    public TimeSpan PollPlaysEvery { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long plays stay in the ledger (the play-history and streak signals look back 90 days).</summary>
    public TimeSpan PlayHistoryRetention { get; set; } = TimeSpan.FromDays(400);

    /// <summary>
    /// ISO 3166-1 country whose releases make up an artist's catalog (ADR 0006). One market keeps each release to
    /// one row instead of one per region.
    /// </summary>
    public string CatalogMarket { get; set; } = "US";

    /// <summary>How often an active artist's catalog is reloaded from Spotify.</summary>
    public TimeSpan CatalogRefreshEvery { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Releases loaded per artist, in the order Spotify lists them (10 per request).</summary>
    public int MaxCatalogAlbums { get; set; } = 500;

    /// <summary>
    /// How long a player request waits for a running sync of the same fan to finish before answering 503. Both
    /// refresh the fan's token, which Spotify may rotate, so they never run together.
    /// </summary>
    public TimeSpan PlayerLockWait { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsEnabled => ClientId.Length > 0;

    /// <summary>Artist search and lookup need an app token, so the client secret too.</summary>
    /// <summary>
    /// Whitespace counts as unset: on GKE a Secret Manager version cannot be empty, so an unconfigured secret is a
    /// single space (infrastructure/README.md).
    /// </summary>
    public bool IsCatalogEnabled => IsEnabled && !string.IsNullOrWhiteSpace(ClientSecret);

    public IReadOnlyList<string> RedirectUriList => Split(RedirectUris);

    public IReadOnlyList<string> PreviousTokenKeyList => Split(PreviousTokenKeys);

    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
