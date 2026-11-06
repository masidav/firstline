namespace FandomScore.MusicProviders;

/// <summary>The listening services besides Spotify (migration 0058). Values are the <c>provider</c> column.</summary>
public static class MusicProvider
{
    public const string AppleMusic = "apple_music";
    public const string Tidal = "tidal";

    public static readonly IReadOnlyList<string> All = [AppleMusic, Tidal];

    public static bool IsKnown(string? provider) => provider is AppleMusic or Tidal;

    /// <summary>The purpose a provider's stored token is encrypted under (the Spotify package's token protector).</summary>
    public static string TokenPurpose(string provider) => $"{provider}-token";
}

/// <summary>
/// Configuration section <c>MusicProviders</c>, read by the API and the worker. The Apple Music key and the Tidal
/// client secret (if any) are secrets and come from the environment. Stored tokens are encrypted with
/// <c>Spotify__TokenKey</c> (one key for every provider's tokens; see ADR 0007).
/// </summary>
public sealed class MusicProviderOptions
{
    public const string Section = "MusicProviders";

    public AppleMusicOptions AppleMusic { get; set; } = new();

    public TidalOptions Tidal { get; set; } = new();

    /// <summary>Automatic sync interval per connection.</summary>
    public TimeSpan SyncEvery { get; set; } = TimeSpan.FromDays(1);

    /// <summary>openapi.yaml promises one manual sync per 15 minutes per fan and provider.</summary>
    public TimeSpan ManualSyncInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Library items read per sync and kind (songs, albums, playlist items): keeps a sync to a few hundred requests.</summary>
    public int MaxLibraryItems { get; set; } = 5000;

    public int MaxPlaylists { get; set; } = 50;

    public int MaxPlaylistItems { get; set; } = 500;

    public bool IsEnabled(string provider) => provider switch
    {
        MusicProvider.AppleMusic => AppleMusic.IsEnabled,
        MusicProvider.Tidal => Tidal.IsEnabled,
        _ => false,
    };
}

/// <summary>
/// Apple Music needs a MusicKit key from the Apple Developer account (Keys, "Media Services"): the team id, the
/// key id, and the <c>.p8</c> private key. The API signs short-lived developer tokens with it (ES256) for the
/// app's MusicKit and for its own calls; the fan's Music User Token comes from MusicKit in the app.
/// </summary>
public sealed class AppleMusicOptions
{
    public string TeamId { get; set; } = "";

    public string KeyId { get; set; } = "";

    /// <summary>The <c>.p8</c> private key, PEM text. Secret.</summary>
    public string PrivateKey { get; set; } = "";

    /// <summary>How long a developer token is good for (Apple allows up to six months; the app gets one per session).</summary>
    public TimeSpan DeveloperTokenLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Whitespace counts as unset (a Secret Manager version cannot be empty).</summary>
    public bool IsEnabled => TeamId.Length > 0 && KeyId.Length > 0 && !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>
/// Tidal: a client from the TIDAL developer dashboard (authorization code flow with PKCE, no secret needed) and the
/// redirect URIs the app may use, each also registered in the dashboard.
/// </summary>
public sealed class TidalOptions
{
    /// <summary>Scopes the app must request. Tidal grants all or none.</summary>
    public static readonly IReadOnlyList<string> RequiredScopes = ["user.read", "collection.read", "playlists.read"];

    public string ClientId { get; set; } = "";

    public string RedirectUris { get; set; } = "";

    /// <summary>ISO 3166-1 country for catalog lookups (Tidal answers per country).</summary>
    public string CountryCode { get; set; } = "US";

    public bool IsEnabled => ClientId.Length > 0;

    public IReadOnlyList<string> RedirectUriList =>
        RedirectUris.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
