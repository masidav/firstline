using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FandomScore.MusicProviders;

/// <summary>
/// The Apple Music API (ADR 0007): developer tokens signed with the MusicKit key, and the reads the sync needs with
/// a fan's Music User Token (library songs with their catalog entries for the ISRC, library albums, library playlists
/// and their songs, recently played, heavy rotation). Apple gives no play times and no user id. Tokens are never
/// logged or put in exception messages. Not yet tried against the real API (see the package README).
/// </summary>
public sealed partial class AppleMusicClient(
    HttpClient http, IOptions<MusicProviderOptions> options, TimeProvider time, ILogger<AppleMusicClient> logger) : IDisposable
{
    public static readonly Uri ApiBase = new("https://api.music.apple.com/v1/");

    private const string Provider = "Apple Music";
    private const int PageSize = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AppleMusicOptions _apple = options.Value.AppleMusic;
    private readonly MusicProviderOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Token, DateTimeOffset ExpiresAt)? _developerToken;
    private ECDsa? _signingKey;

    /// <summary>
    /// A developer token: an ES256 JWT with the team id as issuer and the key id in the header, good for
    /// <see cref="AppleMusicOptions.DeveloperTokenLifetime"/>. Cached per process until a minute before expiry. The
    /// app's MusicKit needs one too (<c>GET /me/music/apple_music/developer-token</c>).
    /// </summary>
    public async Task<string> DeveloperTokenAsync(CancellationToken cancellationToken)
    {
        if (_developerToken is { } current && current.ExpiresAt > time.GetUtcNow())
        {
            return current.Token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_developerToken is { } fresh && fresh.ExpiresAt > time.GetUtcNow())
            {
                return fresh.Token;
            }

            var now = time.GetUtcNow().UtcDateTime;
            var expires = now + _apple.DeveloperTokenLifetime;
            // One key for the life of the client: the token handler caches a signing provider per security key, so a
            // key disposed after the first token would be used again for the next.
            if (_signingKey is null)
            {
                var key = ECDsa.Create();
                key.ImportFromPem(_apple.PrivateKey);
                _signingKey = key;
            }

            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = _apple.TeamId,
                IssuedAt = now,
                NotBefore = now,
                Expires = expires,
                SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_signingKey) { KeyId = _apple.KeyId }, SecurityAlgorithms.EcdsaSha256),
            });
            _developerToken = (token, new DateTimeOffset(expires) - TimeSpan.FromMinutes(1));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Fails startup on a malformed key instead of at the first connection.</summary>
    public static void ValidateKey(AppleMusicOptions apple)
    {
        if (!apple.IsEnabled)
        {
            return;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(apple.PrivateKey);
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException("MusicProviders:AppleMusic:PrivateKey is not a PEM EC private key (.p8).", e);
        }
    }

    /// <summary>Checks a Music User Token by asking for the fan's storefront. Throws <see cref="MusicAuthorizationException"/> when Apple rejects it.</summary>
    public async Task<AppleMusicUser> GetMeAsync(string userToken, CancellationToken cancellationToken)
    {
        var storefront = await GetAsync<Document<Resource>>(userToken, "me/storefront", "storefront", cancellationToken);
        var id = storefront.Data is { Count: > 0 } data ? data[0].Id : null;
        return new AppleMusicUser(string.IsNullOrEmpty(id) ? "us" : id);
    }

    /// <summary>Everything the sync reads about a fan.</summary>
    public async Task<MusicLibrary> GetLibraryAsync(string userToken, CancellationToken cancellationToken)
    {
        var songs = await GetPagesAsync<Resource>(userToken, "me/library/songs?include=catalog", _options.MaxLibraryItems, "library songs", cancellationToken);
        var albums = await GetPagesAsync<Resource>(userToken, "me/library/albums", _options.MaxLibraryItems, "library albums", cancellationToken);
        var playlists = await GetPagesAsync<Resource>(userToken, "me/library/playlists", _options.MaxPlaylists, "library playlists", cancellationToken);
        var ownPlaylists = new List<ProviderPlaylist>();
        foreach (var playlist in playlists.Where(p => p.Attributes?.CanEdit != false))
        {
            var items = await GetPagesAsync<Resource>(userToken,
                $"me/library/playlists/{Uri.EscapeDataString(playlist.Id)}/tracks?include=catalog", _options.MaxPlaylistItems,
                "playlist tracks", cancellationToken);
            ownPlaylists.Add(new ProviderPlaylist(playlist.Id, [.. items.Select(ToTrack).OfType<ProviderTrack>()]));
        }

        var recent = await GetAsync<Document<Resource>>(userToken, "me/recent/played/tracks?limit=30", "recently played", cancellationToken);
        var heavy = await GetAsync<Document<Resource>>(userToken, "me/history/heavy-rotation?limit=10", "heavy rotation", cancellationToken);

        return new MusicLibrary(
            [.. songs.Select(ToTrack).OfType<ProviderTrack>()],
            [.. albums.Select(ToAlbum).OfType<ProviderAlbum>()],
            ownPlaylists,
            [.. (recent.Data ?? []).Select(ToTrack).OfType<ProviderTrack>()],
            // Heavy rotation lists albums and playlists; the artist is what the score ranks.
            [.. (heavy.Data ?? []).Select(r => r.Attributes?.ArtistName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
            []);
    }

    // A library song carries its catalog song (include=catalog) with the ISRC; a catalog song (recently played) has
    // it directly. Songs without a catalog entry (uploads) have no ISRC and match by title only.
    private static ProviderTrack? ToTrack(Resource song)
    {
        if (song.Attributes is not { Name: { Length: > 0 } name, ArtistName: { Length: > 0 } artist })
        {
            return null;
        }

        var catalog = song.Relationships?.Catalog?.Data is { Count: > 0 } entries ? entries[0] : null;
        return new ProviderTrack(song.Id, name, artist, song.Attributes.AlbumName,
            Isrc(song.Attributes.Isrc ?? catalog?.Attributes?.Isrc), song.Attributes.DateAdded);
    }

    private static ProviderAlbum? ToAlbum(Resource album) =>
        album.Attributes is { Name: { Length: > 0 } name, ArtistName: { Length: > 0 } artist }
            ? new ProviderAlbum(album.Id, name, artist, null, album.Attributes.DateAdded)
            : null;

    private static string? Isrc(string? value) => value is { Length: 12 } ? value.ToUpperInvariant() : null;

    private async Task<List<T>> GetPagesAsync<T>(string userToken, string path, int max, string label, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        for (var offset = 0; offset < max; offset += PageSize)
        {
            var page = await GetAsync<Document<T>>(userToken, $"{path}{separator}limit={PageSize}&offset={offset}", label, cancellationToken);
            items.AddRange(page.Data ?? []);
            if (page.Data is not { Count: > 0 } || page.Next is null)
            {
                break;
            }
        }

        return items.Count > max ? items[..max] : items;
    }

    private async Task<T> GetAsync<T>(string userToken, string pathAndQuery, string label, CancellationToken cancellationToken)
    {
        var developerToken = await DeveloperTokenAsync(cancellationToken);
        using var response = await ProviderHttp.SendAsync(http, time, logger, Provider, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, pathAndQuery));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", developerToken);
            request.Headers.Add("Music-User-Token", userToken);
            return request;
        }, label, retryServerErrors: true, cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Apple answers 403 for a user token that was revoked or whose subscription lapsed.
            throw new MusicAuthorizationException($"Apple Music rejected the user token ({label}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new MusicRequestException((int)response.StatusCode, null);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
               ?? throw new MusicUnavailableException($"Apple Music returned an empty {label} response.");
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
        _signingKey?.Dispose();
    }

    private sealed record Document<T>(IReadOnlyList<T>? Data, string? Next);

    private sealed record Resource(string Id, string? Type, Attributes? Attributes, Relationships? Relationships);

    private sealed record Attributes(
        string? Name, string? ArtistName, string? AlbumName, string? Isrc, DateTimeOffset? DateAdded, bool? CanEdit);

    private sealed record Relationships(Document<Resource>? Catalog);
}
