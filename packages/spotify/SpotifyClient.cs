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

    private const int PageSize = 50;
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
    public async Task<SpotifyLibrary> GetLibraryAsync(string accessToken, string spotifyUserId, CancellationToken cancellationToken)
    {
        var saved = await GetPagesAsync<SavedTrackDto>(
            accessToken, "me/tracks", _options.MaxSavedTracks, "saved tracks", cancellationToken);
        var top = await GetAsync<Page<ArtistDto>>(
            accessToken, "me/top/artists?time_range=medium_term&limit=50", "top artists", cancellationToken);
        var recent = await GetAsync<Page<PlayHistoryDto>>(
            accessToken, "me/player/recently-played?limit=50", "recently played", cancellationToken);
        var playlists = await GetPagesAsync<PlaylistDto>(
            accessToken, "me/playlists", _options.MaxPlaylists, "playlists", cancellationToken);

        var playlistTracks = new List<SpotifyTrack>();
        foreach (var playlist in playlists)
        {
            if (playlist.Id is null || !(playlist.Collaborative || playlist.Owner?.Id == spotifyUserId))
            {
                continue;
            }

            var items = await GetPagesAsync<PlaylistItemDto>(accessToken,
                $"playlists/{Uri.EscapeDataString(playlist.Id)}/items", _options.MaxPlaylistItems, "playlist items", cancellationToken);
            playlistTracks.AddRange(items.Select(i => ToTrack(i.Item ?? i.Track)).OfType<SpotifyTrack>());
        }

        return new SpotifyLibrary(
            [.. saved.Select(s => ToTrack(s.Track) is { } track ? new SpotifySavedTrack(track, s.AddedAt) : null)
                .OfType<SpotifySavedTrack>()],
            playlistTracks,
            // Keep positions: the index is the rank.
            [.. (top.Items ?? []).Select(a => a.Id ?? "")],
            [.. (recent.Items ?? []).Select(p => ToTrack(p.Track)).OfType<SpotifyTrack>()]);
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

    private async Task<List<T>> GetPagesAsync<T>(
        string accessToken, string path, int max, string label, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        for (var offset = 0; offset < max; offset += PageSize)
        {
            var page = await GetAsync<Page<T>>(accessToken, $"{path}?limit={PageSize}&offset={offset}", label, cancellationToken);
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

    private sealed record Page<T>(IReadOnlyList<T>? Items, string? Next);

    private sealed record UserDto(string? Id);

    private sealed record ImageDto(string? Url, int? Width);

    private sealed record ArtistDto(string? Id, string? Name, IReadOnlyList<ImageDto>? Images, IReadOnlyList<string>? Genres);

    private sealed record SearchDto(Page<ArtistDto>? Artists);

    private sealed record AlbumDto(string? Id);

    private sealed record TrackDto(string? Id, string? Type, bool? IsLocal, AlbumDto? Album, IReadOnlyList<ArtistDto>? Artists);

    private sealed record SavedTrackDto(DateTimeOffset? AddedAt, TrackDto? Track);

    private sealed record PlayHistoryDto(TrackDto? Track);

    private sealed record OwnerDto(string? Id);

    private sealed record PlaylistDto(string? Id, bool Collaborative, OwnerDto? Owner);

    // Since February 2026 the track is in "item"; "track" is read too in case older responses appear.
    private sealed record PlaylistItemDto(TrackDto? Item, TrackDto? Track);
}
