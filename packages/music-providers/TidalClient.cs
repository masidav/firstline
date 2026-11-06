using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FandomScore.MusicProviders;

/// <summary>
/// TIDAL's token endpoint (authorization code with PKCE, refresh) and the API v2 reads the sync needs: the fan's
/// collection of tracks, albums, artists (favourites), and playlists with their items. The API is JSON:API: a
/// relationship document lists ids (with <c>meta.addedAt</c>) in <c>data</c> and the resources in <c>included</c>;
/// a track's artists come as another relationship, resolved with <c>/tracks?filter[id]=…&amp;include=artists</c>.
/// Tidal publishes no listening history, so there are no plays or top artists here. Tokens and codes are never
/// logged or put in exception messages. Not yet tried against the real API (see the package README).
/// </summary>
public sealed partial class TidalClient(
    HttpClient http, IOptions<MusicProviderOptions> options, TimeProvider time, ILogger<TidalClient> logger)
{
    public static readonly Uri TokenEndpoint = new("https://auth.tidal.com/v1/oauth2/token");
    public static readonly Uri ApiBase = new("https://openapi.tidal.com/v2/");

    private const string Provider = "Tidal";
    private const int LookupBatch = 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TidalOptions _tidal = options.Value.Tidal;
    private readonly MusicProviderOptions _options = options.Value;

    public Task<TidalTokens> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = _tidal.ClientId,
            ["code_verifier"] = codeVerifier,
        }, "code exchange", cancellationToken);

    /// <summary>Tidal may rotate the refresh token: store the new one before doing anything else.</summary>
    public Task<TidalTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _tidal.ClientId,
        }, "token refresh", cancellationToken);

    public async Task<TidalUser> GetMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        var me = await GetAsync<SingleDocument>(accessToken, "users/me", "profile", cancellationToken);
        var id = me.Data?.Id ?? throw new MusicUnavailableException("Tidal returned a profile without an id.");
        return new TidalUser(id, me.Data.Attributes?.Country);
    }

    public async Task<MusicLibrary> GetLibraryAsync(string accessToken, CancellationToken cancellationToken)
    {
        var country = Uri.EscapeDataString(_tidal.CountryCode);
        // Tracks and albums ask for their artists too; when a page leaves them out, the lookup fills them in.
        var tracks = await GetCollectionAsync(accessToken, "userCollectionTracks/me/relationships/items", country, "tracks",
            _options.MaxLibraryItems, "collection tracks", cancellationToken, "items,items.artists");
        var albums = await GetCollectionAsync(accessToken, "userCollectionAlbums/me/relationships/items", country, "albums",
            _options.MaxLibraryItems, "collection albums", cancellationToken, "items,items.artists");
        var artists = await GetCollectionAsync(accessToken, "userCollectionArtists/me/relationships/items", country, "artists",
            _options.MaxLibraryItems, "collection artists", cancellationToken);
        var playlists = await GetCollectionAsync(accessToken, "userCollectionPlaylists/me/relationships/items", country, "playlists",
            _options.MaxPlaylists, "collection playlists", cancellationToken);

        var ownPlaylists = new List<ProviderPlaylist>();
        foreach (var playlist in playlists.Items)
        {
            var items = await GetCollectionAsync(accessToken, $"playlists/{Uri.EscapeDataString(playlist.Resource.Id)}/relationships/items",
                country, "tracks", _options.MaxPlaylistItems, "playlist items", cancellationToken);
            ownPlaylists.Add(new ProviderPlaylist(playlist.Resource.Id, await ToTracksAsync(accessToken, country, items, cancellationToken)));
        }

        return new MusicLibrary(
            await ToTracksAsync(accessToken, country, tracks, cancellationToken),
            await ToAlbumsAsync(accessToken, country, albums, cancellationToken),
            ownPlaylists,
            [],
            [],
            [.. artists.Items.Select(a => a.Resource.Attributes?.Name).OfType<string>()]);
    }

    private sealed record CollectionItem(Resource Resource, DateTimeOffset? AddedAt);

    private sealed record Collection(IReadOnlyList<CollectionItem> Items, IReadOnlyDictionary<string, Resource> Included);

    // Pages a relationship document: data lists {id, type, meta.addedAt}; included carries the resources (and, when
    // asked, their artists). Cursor paging through links.next.
    private async Task<Collection> GetCollectionAsync(
        string accessToken, string path, string country, string type, int max, string label, CancellationToken cancellationToken,
        string include = "items")
    {
        var items = new List<CollectionItem>();
        var included = new Dictionary<string, Resource>(StringComparer.Ordinal);
        var next = $"{path}?countryCode={country}&include={Uri.EscapeDataString(include)}";
        while (items.Count < max)
        {
            var page = await GetAsync<RelationshipDocument>(accessToken, next, label, cancellationToken);
            foreach (var resource in page.Included ?? [])
            {
                included[$"{resource.Type}:{resource.Id}"] = resource;
            }

            foreach (var reference in page.Data ?? [])
            {
                if (reference.Type == type && included.TryGetValue($"{type}:{reference.Id}", out var resource))
                {
                    items.Add(new CollectionItem(resource, reference.Meta?.AddedAt));
                }
            }

            if (page.Data is not { Count: > 0 } || page.Links?.Next is not { Length: > 0 } link)
            {
                break;
            }

            // links.next is a path with the cursor; relative to the API base.
            next = link.StartsWith('/') ? link[1..] : link;
        }

        return new Collection(items.Count > max ? items[..max] : items, included);
    }

    // Artists of tracks and albums come from a lookup by id with include=artists (the collection endpoints don't
    // include them), in batches.
    private async Task<IReadOnlyList<ProviderTrack>> ToTracksAsync(
        string accessToken, string country, Collection tracks, CancellationToken cancellationToken)
    {
        var artists = await ArtistNamesAsync(accessToken, country, "tracks", tracks, cancellationToken);
        return [.. tracks.Items
            .Select(t => t.Resource.Attributes is { Title: { Length: > 0 } title } && artists.TryGetValue(t.Resource.Id, out var artist)
                ? new ProviderTrack(t.Resource.Id, title, artist, null, Isrc(t.Resource.Attributes.Isrc), t.AddedAt)
                : null)
            .OfType<ProviderTrack>()];
    }

    private async Task<IReadOnlyList<ProviderAlbum>> ToAlbumsAsync(
        string accessToken, string country, Collection albums, CancellationToken cancellationToken)
    {
        var artists = await ArtistNamesAsync(accessToken, country, "albums", albums, cancellationToken);
        return [.. albums.Items
            .Select(a => a.Resource.Attributes is { Title: { Length: > 0 } title } && artists.TryGetValue(a.Resource.Id, out var artist)
                ? new ProviderAlbum(a.Resource.Id, title, artist, a.Resource.Attributes.BarcodeId, a.AddedAt)
                : null)
            .OfType<ProviderAlbum>()];
    }

    /// <summary>
    /// Resource id → first credited artist's name, for the tracks or albums of a collection. Artists the page included
    /// are used as they are; anything else (no artist relationship on the item, or an artist the page didn't include)
    /// is looked up by id with <c>include=artists</c>, in batches.
    /// </summary>
    private async Task<Dictionary<string, string>> ArtistNamesAsync(
        string accessToken, string country, string type, Collection collection, CancellationToken cancellationToken)
    {
        var artistsById = collection.Included.Values.Where(r => r.Type == "artists").ToDictionary(r => r.Id, StringComparer.Ordinal);
        var resources = collection.Items.Select(i => i.Resource).ToDictionary(r => r.Id, StringComparer.Ordinal);
        static string? FirstArtistId(Resource resource) =>
            resource.Relationships?.Artists?.Data is { Count: > 0 } credited ? credited[0].Id : null;

        var lookup = resources.Values.Where(r => FirstArtistId(r) is not { } id || !artistsById.ContainsKey(id)).ToList();
        foreach (var batch in lookup.Chunk(LookupBatch))
        {
            var ids = string.Join(',', batch.Select(r => Uri.EscapeDataString(r.Id)));
            var document = await GetAsync<ResourceDocument>(accessToken,
                $"{type}?countryCode={country}&filter[id]={ids}&include=artists", $"{type} lookup", cancellationToken);
            foreach (var artist in (document.Included ?? []).Where(r => r.Type == "artists"))
            {
                artistsById[artist.Id] = artist;
            }

            foreach (var resource in document.Data ?? [])
            {
                resources[resource.Id] = resource;
            }
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, resource) in resources)
        {
            if (FirstArtistId(resource) is { } artistId && artistsById.TryGetValue(artistId, out var artist)
                && artist.Attributes?.Name is { Length: > 0 } name)
            {
                names[id] = name;
            }
        }

        return names;
    }

    private static string? Isrc(string? value) => value is { Length: 12 } ? value.ToUpperInvariant() : null;

    private async Task<TidalTokens> TokenAsync(Dictionary<string, string> form, string label, CancellationToken cancellationToken)
    {
        // A code works once and a refresh may rotate the token, so server errors are not retried here.
        using var response = await ProviderHttp.SendAsync(http, time, logger, Provider,
            () => new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) },
            label, retryServerErrors: false, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            try
            {
                error = (await response.Content.ReadFromJsonAsync<OAuthError>(Json, cancellationToken))?.Error;
            }
            catch (JsonException)
            {
            }

            if (error == "invalid_grant" || response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new MusicAuthorizationException($"Tidal rejected the {label} (invalid_grant).");
            }

            throw new MusicRequestException((int)response.StatusCode, error);
        }

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, cancellationToken);
        if (tokens?.AccessToken is not { Length: > 0 })
        {
            throw new MusicUnavailableException($"Tidal returned an incomplete {label} response.");
        }

        return new TidalTokens(tokens.AccessToken, tokens.RefreshToken,
            (tokens.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            TimeSpan.FromSeconds(tokens.ExpiresIn is > 0 ? tokens.ExpiresIn.Value : 3600));
    }

    private async Task<T> GetAsync<T>(string accessToken, string pathAndQuery, string label, CancellationToken cancellationToken)
    {
        using var response = await ProviderHttp.SendAsync(http, time, logger, Provider, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, pathAndQuery));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
            return request;
        }, label, retryServerErrors: true, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new MusicAuthorizationException($"Tidal rejected the access token ({label}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new MusicRequestException((int)response.StatusCode, null);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
               ?? throw new MusicUnavailableException($"Tidal returned an empty {label} response.");
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        string? Scope,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);

    private sealed record OAuthError(string? Error);

    private sealed record Reference(string Id, string? Type, ReferenceMeta? Meta);

    private sealed record ReferenceMeta(DateTimeOffset? AddedAt);

    private sealed record Links(string? Next);

    private sealed record RelationshipDocument(IReadOnlyList<Reference>? Data, IReadOnlyList<Resource>? Included, Links? Links);

    private sealed record SingleDocument(Resource? Data);

    /// <summary>A lookup by id: the resources themselves in <c>data</c>, their artists in <c>included</c>.</summary>
    private sealed record ResourceDocument(IReadOnlyList<Resource>? Data, IReadOnlyList<Resource>? Included);

    private sealed record Resource(string Id, string? Type, Attributes? Attributes, Relationships? Relationships);

    private sealed record Attributes(string? Title, string? Name, string? Isrc, string? BarcodeId, string? Country);

    private sealed record Relationships(RelationshipDocument? Artists);
}
