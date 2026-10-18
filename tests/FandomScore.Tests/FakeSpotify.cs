using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FandomScore.Spotify;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FandomScore.Tests;

/// <summary>
/// Stands in for accounts.spotify.com and api.spotify.com: serves a configurable library with Spotify's
/// paging, and records every request.
/// </summary>
public sealed class FakeSpotify : HttpMessageHandler
{
    public const string ClientId = "test-client-id";
    public const string RedirectUri = "fandomscore://spotify-callback";

    public static readonly string TokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static readonly string AllScopes = string.Join(' ', SpotifyOptions.RequiredScopes);

    public sealed record Request(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Form, string? Bearer);

    public ConcurrentQueue<Request> Calls { get; } = new();

    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>The token endpoint's JSON body (an error body when <see cref="TokenStatus"/> is not OK).</summary>
    public object TokenResponse { get; set; } = Tokens("refresh-1");

    public string UserId { get; set; } = "sp-user-1";

    public HttpStatusCode ApiStatus { get; set; } = HttpStatusCode.OK;

    public List<object> SavedTracks { get; } = [];

    public List<string> TopArtistIds { get; } = [];

    public List<object> RecentlyPlayed { get; } = [];

    public List<object> Playlists { get; } = [];

    public Dictionary<string, List<object>> PlaylistItems { get; } = [];

    /// <summary>Served once each, in order, before the normal response for that path.</summary>
    public ConcurrentQueue<(string Path, Func<HttpResponseMessage> Response)> Interruptions { get; } = new();

    /// <summary>Called before each request is answered, with its path.</summary>
    public Func<string, Task>? OnRequest { get; set; }

    public static object Tokens(string? refreshToken, string? scope = null) =>
        new { access_token = "access-1", token_type = "Bearer", expires_in = 3600, refresh_token = refreshToken, scope = scope ?? AllScopes };

    public static object Track(string id, string album, params string[] artistIds) => new
    {
        id,
        type = "track",
        is_local = false,
        album = new { id = album },
        artists = artistIds.Select(a => new { id = a }).ToArray(),
    };

    public static object Saved(object track, string addedAt = "2026-01-02T03:04:05Z") => new { added_at = addedAt, track };

    public static object Played(object track) => new { track, played_at = "2026-10-01T00:00:00Z" };

    public static object Playlist(string id, string owner, bool collaborative = false) =>
        new { id, collaborative, owner = new { id = owner } };

    public static object PlaylistItem(object track) => new { item = track };

    public static SpotifyOptions Options() => new()
    {
        ClientId = ClientId,
        RedirectUris = $"{RedirectUri},https://example.test/callback",
        TokenKey = TokenKey,
    };

    public SpotifyClient Client(SpotifyOptions? options = null) =>
        new(new HttpClient(this, disposeHandler: false), Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            TimeProvider.System, NullLogger<SpotifyClient>.Instance);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var form = request.Content is FormUrlEncodedContent content
            ? (await content.ReadAsStringAsync(cancellationToken)).Split('&').Select(pair => pair.Split('='))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')))
            : [];
        var path = uri.Host == "accounts.spotify.com" ? "token" : uri.AbsolutePath["/v1/".Length..];
        Calls.Enqueue(new Request(request.Method.Method, path, uri.Query, form, request.Headers.Authorization?.Parameter));
        if (OnRequest is not null)
        {
            await OnRequest(path);
        }

        if (Interruptions.TryPeek(out var next) && next.Path == path && Interruptions.TryDequeue(out next))
        {
            return next.Response();
        }

        if (path == "token")
        {
            return new HttpResponseMessage(TokenStatus) { Content = JsonContent.Create(TokenResponse) };
        }

        if (ApiStatus != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(ApiStatus) { Content = JsonContent.Create(new { error = new { status = (int)ApiStatus } }) };
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var limit = int.Parse(query["limit"] ?? "20", System.Globalization.CultureInfo.InvariantCulture);
        var offset = int.Parse(query["offset"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        object body = path switch
        {
            "me" => new { id = UserId, display_name = "Fan" },
            "me/tracks" => Page(SavedTracks, limit, offset),
            "me/top/artists" => Page(TopArtistIds.Select(id => (object)new { id, name = id }).ToList(), limit, offset),
            "me/player/recently-played" => new { items = RecentlyPlayed.Take(limit).ToArray(), next = (string?)null },
            "me/playlists" => Page(Playlists, limit, offset),
            _ when path.StartsWith("playlists/", StringComparison.Ordinal) && path.EndsWith("/items", StringComparison.Ordinal) =>
                Page(PlaylistItems.GetValueOrDefault(path["playlists/".Length..^"/items".Length]) ?? [], limit, offset),
            _ => throw new InvalidOperationException($"FakeSpotify has no route for {path}"),
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    private static object Page(List<object> items, int limit, int offset) => new
    {
        items = items.Skip(offset).Take(limit).ToArray(),
        total = items.Count,
        next = offset + limit < items.Count ? $"https://api.spotify.com/v1/next?offset={offset + limit}" : null,
    };

    public static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        }

        return response;
    }
}
