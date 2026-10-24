using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FandomScore.Spotify;

/// <summary>
/// Spotify's token endpoint (authorization code with PKCE, refresh, client credentials), the Web API reads the
/// sync needs, and artist search and lookup with an app token.
/// Only endpoints available to development-mode apps since February 2026 are used. Reads retry server errors
/// and short rate limits a few times; longer waits surface as <see cref="SpotifyUnavailableException"/> so the
/// job queue retries later. Tokens and codes are never logged or put in exception messages.
/// </summary>
public sealed partial class SpotifyClient(
    HttpClient http, IOptions<SpotifyOptions> options, SpotifyAppToken appToken, TimeProvider time, ILogger<SpotifyClient> logger)
{
    public static readonly Uri TokenEndpoint = new("https://accounts.spotify.com/api/token");
    public static readonly Uri ApiBase = new("https://api.spotify.com/v1/");
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Spotify's cap on search results per request for development-mode apps since February 2026.</summary>
    public const int MaxSearchLimit = 10;

    /// <summary>Spotify's cap per request on an artist's albums (same February 2026 change).</summary>
    public const int AlbumPageSize = 10;

    private const int PageSize = 50;
    private const int MaxAlbumTracks = 500;   // 10 discs of 50; no real album is longer
    private const int MaxTries = 3;
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly SpotifyOptions _options = options.Value;

    /// <summary>Exchanges the code the app got from Spotify's authorization (PKCE, no client secret).</summary>
    public Task<SpotifyTokens> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = _options.ClientId,
            ["code_verifier"] = codeVerifier,
        }, "code exchange", cancellationToken);

    /// <summary>
    /// Gets a new access token. Spotify may also return a new refresh token, after which the old one can stop
    /// working: store it before doing anything else.
    /// </summary>
    public Task<SpotifyTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId,
        }, "token refresh", cancellationToken);

    public async Task<SpotifyUser> GetMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        var me = await GetAsync<UserDto>(accessToken, "me", "profile", cancellationToken);
        return new SpotifyUser(me.Id ?? throw new SpotifyUnavailableException("Spotify returned a profile without an id."));
    }

    /// <summary>
    /// Saved tracks, top artists (about six months), the last 50 plays, and tracks in playlists the user owns or
    /// collaborates on (Spotify returns no items for other playlists). Capped by <see cref="SpotifyOptions"/>.
    /// </summary>
    /// <summary>
    /// Everything the sync reads about a fan. <paramref name="includeFollowed"/> is false for connections made
    /// without scope <see cref="SpotifyOptions.FollowScope"/>, which Spotify would answer with 403.
    /// </summary>
    public async Task<SpotifyLibrary> GetLibraryAsync(
        string accessToken, string spotifyUserId, bool includeFollowed, CancellationToken cancellationToken)
    {
        var saved = await GetPagesAsync<SavedTrackDto>(
            accessToken, "me/tracks", _options.MaxSavedTracks, "saved tracks", cancellationToken);
        var topMedium = await GetTopArtistsAsync(accessToken, "medium_term", cancellationToken);
        var topShort = await GetTopArtistsAsync(accessToken, "short_term", cancellationToken);
        var topLong = await GetTopArtistsAsync(accessToken, "long_term", cancellationToken);
        var topTracks = new List<SpotifyTrack>();
        foreach (var range in new[] { "short_term", "medium_term", "long_term" })
        {
            var page = await GetAsync<Page<TrackDto>>(
                accessToken, $"me/top/tracks?time_range={range}&limit=50", "top tracks", cancellationToken);
            topTracks.AddRange((page.Items ?? []).Select(ToTrack).OfType<SpotifyTrack>());
        }

        var recent = await GetRecentlyPlayedAsync(accessToken, after: null, cancellationToken);
        var savedAlbums = await GetPagesAsync<SavedAlbumDto>(
            accessToken, "me/albums", _options.MaxSavedAlbums, "saved albums", cancellationToken);
        var followed = includeFollowed ? await GetFollowedArtistIdsAsync(accessToken, cancellationToken) : null;
        var playlists = await GetPagesAsync<PlaylistDto>(
            accessToken, "me/playlists", _options.MaxPlaylists, "playlists", cancellationToken);

        var ownPlaylists = new List<SpotifyPlaylist>();
        foreach (var playlist in playlists)
        {
            if (playlist.Id is null || !(playlist.Collaborative || playlist.Owner?.Id == spotifyUserId))
            {
                continue;
            }

            var items = await GetPagesAsync<PlaylistItemDto>(accessToken,
                $"playlists/{Uri.EscapeDataString(playlist.Id)}/items", _options.MaxPlaylistItems, "playlist items", cancellationToken);
            ownPlaylists.Add(new SpotifyPlaylist(playlist.Id, playlist.Collaborative,
                [.. items.Select(i => ToTrack(i.Item ?? i.Track)).OfType<SpotifyTrack>()]));
        }

        return new SpotifyLibrary(
            [.. saved.Select(s => ToTrack(s.Track) is { } track ? new SpotifySavedTrack(track, s.AddedAt) : null)
                .OfType<SpotifySavedTrack>()],
            ownPlaylists,
            topMedium,
            topShort,
            topLong,
            [.. topTracks.DistinctBy(t => t.Id)],
            [.. savedAlbums.Select(a => a.Album is { Id: { } id }
                    ? new SpotifySavedAlbum(id, [.. (a.Album.Artists ?? []).Select(x => x.Id).OfType<string>()], a.AddedAt)
                    : null)
                .OfType<SpotifySavedAlbum>()],
            followed,
            recent);
    }

    /// <summary>
    /// The fan's most recent plays (Spotify keeps 50), newest first; only plays after <paramref name="after"/>
    /// (milliseconds since the epoch) when given. Used hourly to extend the play ledger.
    /// </summary>
    public async Task<IReadOnlyList<SpotifyPlay>> GetRecentlyPlayedAsync(
        string accessToken, long? after, CancellationToken cancellationToken)
    {
        var query = after is { } cursor ? $"me/player/recently-played?limit=50&after={cursor}" : "me/player/recently-played?limit=50";
        var page = await GetAsync<Page<PlayHistoryDto>>(accessToken, query, "recently played", cancellationToken);
        return [.. (page.Items ?? [])
            .Select(p => ToTrack(p.Track) is { } track && p.PlayedAt is { } playedAt ? new SpotifyPlay(track, playedAt) : null)
            .OfType<SpotifyPlay>()];
    }

    private async Task<IReadOnlyList<SpotifyArtist>> GetTopArtistsAsync(string accessToken, string range, CancellationToken cancellationToken)
    {
        var page = await GetAsync<Page<ArtistDto>>(
            accessToken, $"me/top/artists?time_range={range}&limit=50", "top artists", cancellationToken);
        // Keep positions: the index is the rank. Names and images let the sync track new ones.
        return [.. (page.Items ?? []).Select(ToArtist).OfType<SpotifyArtist>()];
    }

    // Cursor paging (after=<last id>) rather than offsets.
    private async Task<IReadOnlyList<string>> GetFollowedArtistIdsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? after = null;
        while (ids.Count < _options.MaxFollowedArtists)
        {
            var query = after is null ? "me/following?type=artist&limit=50" : $"me/following?type=artist&limit=50&after={Uri.EscapeDataString(after)}";
            var page = (await GetAsync<FollowingDto>(accessToken, query, "followed artists", cancellationToken)).Artists;
            var batch = (page?.Items ?? []).Select(a => a.Id).OfType<string>().ToList();
            ids.AddRange(batch);
            after = page?.Cursors?.After;
            if (batch.Count == 0 || after is null)
            {
                break;
            }
        }

        return ids;
    }

    /// <summary>
    /// Artists matching <paramref name="query"/>, best match first, at most <see cref="MaxSearchLimit"/>.
    /// Needs <see cref="SpotifyOptions.ClientSecret"/>.
    /// </summary>
    public async Task<IReadOnlyList<SpotifyArtist>> SearchArtistsAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var result = await AppGetAsync<SearchDto>(
            $"search?type=artist&limit={Math.Clamp(limit, 1, MaxSearchLimit)}&q={Uri.EscapeDataString(query)}",
            "artist search", cancellationToken);
        return [.. (result.Artists?.Items ?? []).Select(ToArtist).OfType<SpotifyArtist>()];
    }

    /// <summary>One artist, or null when Spotify has none with this id. Needs <see cref="SpotifyOptions.ClientSecret"/>.</summary>
    public async Task<SpotifyArtist?> GetArtistAsync(string spotifyArtistId, CancellationToken cancellationToken)
    {
        try
        {
            return ToArtist(await AppGetAsync<ArtistDto>(
                $"artists/{Uri.EscapeDataString(spotifyArtistId)}", "artist", cancellationToken));
        }
        catch (SpotifyRequestException e) when (e.Status is 400 or 404)
        {
            // 400 is Spotify's answer for an id that cannot exist.
            return null;
        }
    }

    /// <summary>
    /// The artist's own releases (albums, singles, compilations; not <c>appears_on</c>) available in
    /// <paramref name="market"/>, in Spotify's order, at most <paramref name="max"/>. Null when Spotify has no
    /// artist with this id. Needs <see cref="SpotifyOptions.ClientSecret"/>. See ADR 0006.
    /// </summary>
    public async Task<IReadOnlyList<SpotifyAlbum>?> GetArtistAlbumsAsync(
        string spotifyArtistId, string market, int max, CancellationToken cancellationToken)
    {
        var path = $"artists/{Uri.EscapeDataString(spotifyArtistId)}/albums"
                   + $"?include_groups=album,single,compilation&market={Uri.EscapeDataString(market)}";
        try
        {
            var albums = await GetPagesAsync<AlbumDto>(null, path, max, AlbumPageSize, "artist albums", cancellationToken);
            return [.. albums.Select(ToAlbum).OfType<SpotifyAlbum>()];
        }
        catch (SpotifyRequestException e) when (e.Status is 400 or 404)
        {
            return null;
        }
    }

    /// <summary>
    /// How many releases (albums, singles, compilations) the artist has, from one small request. Null when Spotify has
    /// no artist with this id. Spotify no longer gives apps follower counts or popularity, so this is what tells
    /// same-named artists apart. Needs <see cref="SpotifyOptions.ClientSecret"/>.
    /// </summary>
    public async Task<int?> CountArtistReleasesAsync(string spotifyArtistId, CancellationToken cancellationToken)
    {
        try
        {
            var page = await AppGetAsync<Page<AlbumDto>>(
                $"artists/{Uri.EscapeDataString(spotifyArtistId)}/albums?include_groups=album,single,compilation&limit=1",
                "artist release count", cancellationToken);
            return page.Total;
        }
        catch (SpotifyRequestException e) when (e.Status is 400 or 404)
        {
            return null;
        }
    }

    /// <summary>
    /// An album's tracks in disc and track order, or null when Spotify no longer has the album.
    /// Needs <see cref="SpotifyOptions.ClientSecret"/>.
    /// </summary>
    public async Task<IReadOnlyList<SpotifyAlbumTrack>?> GetAlbumTracksAsync(
        string spotifyAlbumId, string market, CancellationToken cancellationToken)
    {
        var path = $"albums/{Uri.EscapeDataString(spotifyAlbumId)}/tracks?market={Uri.EscapeDataString(market)}";
        try
        {
            var tracks = await GetPagesAsync<TrackDto>(null, path, MaxAlbumTracks, PageSize, "album tracks", cancellationToken);
            return [.. tracks.Select(ToAlbumTrack).OfType<SpotifyAlbumTrack>()
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)];
        }
        catch (SpotifyRequestException e) when (e.Status is 400 or 404)
        {
            return null;
        }
    }

    private async Task<T> AppGetAsync<T>(string pathAndQuery, string label, CancellationToken cancellationToken)
    {
        var token = await AppTokenAsync(cancellationToken);
        try
        {
            return await GetAsync<T>(token, pathAndQuery, label, cancellationToken);
        }
        catch (SpotifyAuthorizationException)
        {
            // Expired early or revoked: one more try with a new token.
            appToken.Invalidate(token);
            return await GetAsync<T>(await AppTokenAsync(cancellationToken), pathAndQuery, label, cancellationToken);
        }
    }

    private Task<string> AppTokenAsync(CancellationToken cancellationToken) => appToken.GetAsync(time, async ct =>
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        var tokens = await RequestTokenAsync(
            new Dictionary<string, string> { ["grant_type"] = "client_credentials" }, "app token", ct,
            new AuthenticationHeaderValue("Basic", credentials));
        return (tokens.AccessToken!, TimeSpan.FromSeconds(tokens.ExpiresIn ?? 3600));
    }, cancellationToken);

    private async Task<SpotifyTokens> TokenAsync(
        Dictionary<string, string> form, string label, CancellationToken cancellationToken)
    {
        var tokens = await RequestTokenAsync(form, label, cancellationToken);
        return new SpotifyTokens(tokens.AccessToken!, tokens.RefreshToken,
            (tokens.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<TokenDto> RequestTokenAsync(
        Dictionary<string, string> form, string label, CancellationToken cancellationToken,
        AuthenticationHeaderValue? authorization = null)
    {
        // A code works once and a refresh may rotate the token, so server errors are not retried here.
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Authorization = authorization;
            return request;
        }, label, retryServerErrors: false, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadOAuthErrorAsync(response, cancellationToken);
            if (error == "invalid_grant")
            {
                throw new SpotifyAuthorizationException($"Spotify rejected the {label} (invalid_grant).");
            }

            throw new SpotifyRequestException((int)response.StatusCode, error);
        }

        var tokens = await response.Content.ReadFromJsonAsync<TokenDto>(Json, cancellationToken);
        if (tokens?.AccessToken is not { Length: > 0 })
        {
            throw new SpotifyUnavailableException($"Spotify returned an incomplete {label} response.");
        }

        return tokens;
    }

    private Task<List<T>> GetPagesAsync<T>(
        string accessToken, string path, int max, string label, CancellationToken cancellationToken) =>
        GetPagesAsync<T>(accessToken, path, max, PageSize, label, cancellationToken);

    /// <summary>Follows Spotify's offset paging. A null <paramref name="accessToken"/> uses the app token.</summary>
    private async Task<List<T>> GetPagesAsync<T>(
        string? accessToken, string path, int max, int pageSize, string label, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        for (var offset = 0; offset < max; offset += pageSize)
        {
            var pathAndQuery = $"{path}{separator}limit={pageSize}&offset={offset}";
            var page = accessToken is null
                ? await AppGetAsync<Page<T>>(pathAndQuery, label, cancellationToken)
                : await GetAsync<Page<T>>(accessToken, pathAndQuery, label, cancellationToken);
            items.AddRange(page.Items ?? []);
            if (page.Items is not { Count: > 0 } || page.Next is null)
            {
                break;
            }
        }

        return items.Count > max ? items[..max] : items;
    }

    private async Task<T> GetAsync<T>(string accessToken, string pathAndQuery, string label, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, pathAndQuery));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return request;
        }, label, retryServerErrors: true, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SpotifyAuthorizationException($"Spotify rejected the access token ({label}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SpotifyRequestException((int)response.StatusCode, null);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
               ?? throw new SpotifyUnavailableException($"Spotify returned an empty {label} response.");
    }

    /// <summary>Retries 429 (when Spotify asks for a short wait), and server errors and timeouts if allowed.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest, string label, bool retryServerErrors, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = createRequest();
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (!retryServerErrors || attempt >= MaxTries)
                {
                    throw new SpotifyUnavailableException($"Spotify {label} request failed or timed out.", inner: e);
                }

                LogRetry(logger, label, 0, attempt);
                await Task.Delay(Backoff(attempt), time, cancellationToken);
                continue;
            }

            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfter(response);
                response.Dispose();
                if (wait > MaxInlineWait || attempt >= MaxTries)
                {
                    throw new SpotifyUnavailableException($"Spotify rate limited the {label} request.", wait);
                }

                LogRetry(logger, label, status, attempt);
                await Task.Delay(wait, time, cancellationToken);
                continue;
            }

            if (status >= 500)
            {
                response.Dispose();
                if (!retryServerErrors || attempt >= MaxTries)
                {
                    throw new SpotifyUnavailableException($"Spotify returned {status} for the {label} request.");
                }

                LogRetry(logger, label, status, attempt);
                await Task.Delay(Backoff(attempt), time, cancellationToken);
                continue;
            }

            return response;
        }
    }

    private TimeSpan RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - time.GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
        _ => DefaultRetryAfter,
    };

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(attempt);

    private static async Task<string?> ReadOAuthErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<OAuthErrorDto>(Json, cancellationToken);
            return body?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Episodes and local files have no usable track id; they are skipped.
    private static SpotifyTrack? ToTrack(TrackDto? track) =>
        track is { Id: { } id } && track.Type is null or "track" && track.IsLocal != true
            ? new SpotifyTrack(id, track.Album?.Id, [.. (track.Artists ?? []).Select(a => a.Id).OfType<string>()])
            : null;

    private static SpotifyAlbum? ToAlbum(AlbumDto? album) =>
        album is { Id: { } id, Name: { } name }
            ? new SpotifyAlbum(id, name, album.AlbumType ?? "album", album.ReleaseDate, album.ReleaseDatePrecision,
                album.Images?.Where(i => i.Url is not null).OrderByDescending(i => i.Width ?? 0).FirstOrDefault()?.Url,
                [.. (album.Artists ?? []).Select(a => a.Id).OfType<string>()])
            : null;

    private static SpotifyAlbumTrack? ToAlbumTrack(TrackDto? track) =>
        track is { Id: { } id, Name: { } name } && track.Type is null or "track" && track.IsLocal != true
            ? new SpotifyAlbumTrack(id, name, track.DiscNumber ?? 1, track.TrackNumber ?? 0)
            : null;

    private static SpotifyArtist? ToArtist(ArtistDto? artist) =>
        artist is { Id: { } id, Name: { } name }
            ? new SpotifyArtist(id, name, PickImage(artist.Images), artist.Genres ?? [])
            : null;

    // Big enough for a list row or header without downloading the 640 px original; else the largest there is.
    private static string? PickImage(IReadOnlyList<ImageDto>? images) =>
        images?.Where(i => i.Url is not null)
            .OrderBy(i => i.Width >= 300 ? i.Width : int.MaxValue)
            .ThenByDescending(i => i.Width ?? 0)
            .FirstOrDefault()?.Url;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Spotify {Request} request got status {Status} on attempt {Attempt}; retrying.")]
    private static partial void LogRetry(ILogger logger, string request, int status, int attempt);

    private sealed record TokenDto(string? AccessToken, string? RefreshToken, string? Scope, int? ExpiresIn);

    private sealed record OAuthErrorDto(string? Error);

    private sealed record Page<T>(IReadOnlyList<T>? Items, string? Next, int? Total = null);

    private sealed record UserDto(string? Id);

    private sealed record ImageDto(string? Url, int? Width);

    private sealed record ArtistDto(string? Id, string? Name, IReadOnlyList<ImageDto>? Images, IReadOnlyList<string>? Genres);

    private sealed record SearchDto(Page<ArtistDto>? Artists);

    private sealed record AlbumDto(
        string? Id, string? Name, string? AlbumType, string? ReleaseDate, string? ReleaseDatePrecision,
        IReadOnlyList<ImageDto>? Images, IReadOnlyList<ArtistDto>? Artists);

    private sealed record TrackDto(
        string? Id, string? Name, string? Type, bool? IsLocal, int? DiscNumber, int? TrackNumber, AlbumDto? Album,
        IReadOnlyList<ArtistDto>? Artists);

    private sealed record SavedTrackDto(DateTimeOffset? AddedAt, TrackDto? Track);

    private sealed record PlayHistoryDto(TrackDto? Track, DateTimeOffset? PlayedAt);

    private sealed record SavedAlbumDto(DateTimeOffset? AddedAt, AlbumDto? Album);

    private sealed record CursorsDto(string? After);

    private sealed record CursorPage<T>(IReadOnlyList<T>? Items, CursorsDto? Cursors);

    private sealed record FollowingDto(CursorPage<ArtistDto>? Artists);

    private sealed record OwnerDto(string? Id);

    private sealed record PlaylistDto(string? Id, bool Collaborative, OwnerDto? Owner);

    // Since February 2026 the track is in "item"; "track" is read too in case older responses appear.
    private sealed record PlaylistItemDto(TrackDto? Item, TrackDto? Track);
}
