using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FandomScore.MusicProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FandomScore.Tests;

/// <summary>Stands in for api.music.apple.com: a configurable library with Apple's offset paging; records every request.</summary>
public sealed class FakeAppleMusic : HttpMessageHandler
{
    public const string TeamId = "TEAM123456";
    public const string KeyId = "MUSICKEY01";

    public static readonly string PrivateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();

    public sealed record Request(string Path, string Query, string? Bearer, string? UserToken);

    public ConcurrentQueue<Request> Calls { get; } = new();

    /// <summary>User tokens Apple accepts; any other answers 403.</summary>
    public HashSet<string> ValidUserTokens { get; } = ["mut-1"];

    public string Storefront { get; set; } = "us";

    public HttpStatusCode ApiStatus { get; set; } = HttpStatusCode.OK;

    public List<object> LibrarySongs { get; } = [];

    public List<object> LibraryAlbums { get; } = [];

    public List<object> LibraryPlaylists { get; } = [];

    public Dictionary<string, List<object>> PlaylistTracks { get; } = [];

    public List<object> RecentlyPlayed { get; } = [];

    public List<object> HeavyRotation { get; } = [];

    public static object Song(string id, string name, string artist, string? album = null, string? isrc = null, string? dateAdded = null) => new
    {
        id,
        type = "library-songs",
        attributes = new { name, artistName = artist, albumName = album, dateAdded },
        relationships = isrc is null ? null : new
        {
            catalog = new { data = new[] { new { id = "cat-" + id, type = "songs", attributes = new { isrc } } } },
        },
    };

    public static object CatalogSong(string id, string name, string artist, string isrc) =>
        new { id, type = "songs", attributes = new { name, artistName = artist, isrc } };

    public static object Album(string id, string name, string artist, string? dateAdded = null) =>
        new { id, type = "library-albums", attributes = new { name, artistName = artist, dateAdded } };

    public static object Playlist(string id, string name, bool canEdit = true) =>
        new { id, type = "library-playlists", attributes = new { name, canEdit } };

    public static object HeavyRotationAlbum(string id, string name, string artist) =>
        new { id, type = "albums", attributes = new { name, artistName = artist } };

    public static MusicProviderOptions Options(string? tidalClientId = null) => new()
    {
        AppleMusic = new AppleMusicOptions { TeamId = TeamId, KeyId = KeyId, PrivateKey = PrivateKey },
        Tidal = new TidalOptions { ClientId = tidalClientId ?? FakeTidal.ClientId, RedirectUris = FakeTidal.RedirectUri },
    };

    public AppleMusicClient Client(MusicProviderOptions? options = null)
    {
        var settings = Microsoft.Extensions.Options.Options.Create(options ?? Options());
        return new AppleMusicClient(new HttpClient(this, disposeHandler: false), settings,
            new AppleMusicDeveloperTokens(settings, TimeProvider.System), TimeProvider.System, NullLogger<AppleMusicClient>.Instance);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath["/v1/".Length..];
        var userToken = request.Headers.TryGetValues("Music-User-Token", out var values) ? values.First() : null;
        Calls.Enqueue(new Request(path, uri.Query, request.Headers.Authorization?.Parameter, userToken));

        if (userToken is null || !ValidUserTokens.Contains(userToken))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }

        if (ApiStatus != HttpStatusCode.OK)
        {
            return Task.FromResult(new HttpResponseMessage(ApiStatus));
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var limit = int.Parse(query["limit"] ?? "25", System.Globalization.CultureInfo.InvariantCulture);
        var offset = int.Parse(query["offset"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        object body = path switch
        {
            "me/storefront" => new { data = new[] { new { id = Storefront, type = "storefronts" } } },
            "me/library/songs" => Page(LibrarySongs, limit, offset),
            "me/library/albums" => Page(LibraryAlbums, limit, offset),
            "me/library/playlists" => Page(LibraryPlaylists, limit, offset),
            "me/recent/played/tracks" => new { data = RecentlyPlayed.Take(limit).ToArray() },
            "me/history/heavy-rotation" => new { data = HeavyRotation.Take(limit).ToArray() },
            _ when path.StartsWith("me/library/playlists/", StringComparison.Ordinal) && path.EndsWith("/tracks", StringComparison.Ordinal) =>
                Page(PlaylistTracks.GetValueOrDefault(path["me/library/playlists/".Length..^"/tracks".Length]) ?? [], limit, offset),
            _ => throw new InvalidOperationException($"FakeAppleMusic has no route for {path}"),
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
    }

    private static object Page(List<object> items, int limit, int offset) => new
    {
        data = items.Skip(offset).Take(limit).ToArray(),
        next = offset + limit < items.Count ? $"/v1/me/next?offset={offset + limit}" : null,
    };
}
