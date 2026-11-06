using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FandomScore.MusicProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace FandomScore.Tests;

/// <summary>
/// Stands in for auth.tidal.com and openapi.tidal.com/v2: the token endpoint, the fan's collection as JSON:API
/// relationship documents (ids with addedAt in data, resources in included, cursor paging), and the track and album
/// lookups that carry the artists. Records every request.
/// </summary>
public sealed class FakeTidal : HttpMessageHandler
{
    public const string ClientId = "tidal-client-1";
    public const string RedirectUri = "fandomscore://tidal-callback";
    public static readonly string AllScopes = string.Join(' ', TidalOptions.RequiredScopes);

    public sealed record Request(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Form, string? Bearer);

    public sealed record Artist(string Id, string Name);

    public sealed record Track(string Id, string Title, string? Isrc, Artist Artist);

    public sealed record Album(string Id, string Title, string? Barcode, Artist Artist);

    public ConcurrentQueue<Request> Calls { get; } = new();

    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    public object TokenResponse { get; set; } = Tokens("tidal-refresh-1");

    public string UserId { get; set; } = "td-user-1";

    public HttpStatusCode ApiStatus { get; set; } = HttpStatusCode.OK;

    public List<(Track Track, string? AddedAt)> CollectionTracks { get; } = [];

    public List<(Album Album, string? AddedAt)> CollectionAlbums { get; } = [];

    public List<Artist> CollectionArtists { get; } = [];

    /// <summary>Playlist id → its tracks.</summary>
    public Dictionary<string, List<(Track Track, string? AddedAt)>> Playlists { get; } = [];

    public static object Tokens(string? refreshToken, string? scope = null) =>
        new { access_token = "tidal-access-1", token_type = "Bearer", expires_in = 3600, refresh_token = refreshToken, scope = scope ?? AllScopes };

    public TidalClient Client(MusicProviderOptions? options = null) =>
        new(new HttpClient(this, disposeHandler: false), Microsoft.Extensions.Options.Options.Create(options ?? FakeAppleMusic.Options()),
            TimeProvider.System, NullLogger<TidalClient>.Instance);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var form = request.Content is FormUrlEncodedContent content
            ? (await content.ReadAsStringAsync(cancellationToken)).Split('&').Select(pair => pair.Split('='))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')))
            : [];
        var path = uri.Host == "auth.tidal.com" ? "token" : uri.AbsolutePath["/v2/".Length..];
        Calls.Enqueue(new Request(request.Method.Method, path, uri.Query, form, request.Headers.Authorization?.Parameter));

        if (path == "token")
        {
            return new HttpResponseMessage(TokenStatus) { Content = JsonContent.Create(TokenResponse) };
        }

        if (ApiStatus != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(ApiStatus) { Content = JsonContent.Create(new { errors = new[] { new { status = (int)ApiStatus } } }) };
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var cursor = int.Parse(query["page[cursor]"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        object body = path switch
        {
            "users/me" => new { data = new { id = UserId, type = "users", attributes = new { username = "fan", country = "US" } } },
            "userCollectionTracks/me/relationships/items" => Relationship(path, "tracks", CollectionTracks.Select(t => (t.Track.Id, t.AddedAt, TrackResource(t.Track))), cursor),
            "userCollectionAlbums/me/relationships/items" => Relationship(path, "albums", CollectionAlbums.Select(a => (a.Album.Id, a.AddedAt, AlbumResource(a.Album))), cursor),
            "userCollectionArtists/me/relationships/items" => Relationship(path, "artists", CollectionArtists.Select(a => (a.Id, (string?)null, ArtistResource(a))), cursor),
            "userCollectionPlaylists/me/relationships/items" => Relationship(path, "playlists",
                Playlists.Keys.Select(id => (id, (string?)null, (object)new { id, type = "playlists", attributes = new { name = id } })), cursor),
            "tracks" => Lookup(query["filter[id]"], CollectionTracks.Select(t => t.Track).Concat(Playlists.Values.SelectMany(p => p.Select(t => t.Track)))
                .DistinctBy(t => t.Id).ToDictionary(t => t.Id, t => (TrackResource(t), ArtistResource(t.Artist)))),
            "albums" => Lookup(query["filter[id]"], CollectionAlbums.Select(a => a.Album).DistinctBy(a => a.Id)
                .ToDictionary(a => a.Id, a => (AlbumResource(a), ArtistResource(a.Artist)))),
            _ when path.StartsWith("playlists/", StringComparison.Ordinal) && path.EndsWith("/relationships/items", StringComparison.Ordinal) =>
                Relationship(path, "tracks", (Playlists.GetValueOrDefault(path["playlists/".Length..^"/relationships/items".Length]) ?? [])
                    .Select(t => (t.Track.Id, t.AddedAt, TrackResource(t.Track))), cursor),
            _ => throw new InvalidOperationException($"FakeTidal has no route for {path}"),
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    private const int PageSize = 20;

    // The collection endpoints include the items but not their artists (as Tidal's do); the lookups include both.
    private static object Relationship(string path, string type, IEnumerable<(string Id, string? AddedAt, object Resource)> all, int cursor)
    {
        var items = all.ToList();
        var page = items.Skip(cursor).Take(PageSize).ToList();
        return new
        {
            data = page.Select(i => new { id = i.Id, type, meta = new { addedAt = i.AddedAt } }).ToArray(),
            included = page.Select(i => i.Resource).ToArray(),
            links = new { next = cursor + PageSize < items.Count ? $"/{path}?countryCode=US&include=items&page[cursor]={cursor + PageSize}" : null },
        };
    }

    private static object Lookup(string? ids, Dictionary<string, (object Resource, object Artist)> known)
    {
        var wanted = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(known.ContainsKey).ToList();
        return new
        {
            data = wanted.Select(id => known[id].Resource).ToArray(),
            included = wanted.Select(id => known[id].Artist).DistinctBy(a => ((dynamic)a).id).ToArray(),
        };
    }

    private static object TrackResource(Track track) => new
    {
        id = track.Id,
        type = "tracks",
        attributes = new { title = track.Title, isrc = track.Isrc },
        relationships = new { artists = new { data = new[] { new { id = track.Artist.Id, type = "artists" } } } },
    };

    private static object AlbumResource(Album album) => new
    {
        id = album.Id,
        type = "albums",
        attributes = new { title = album.Title, barcodeId = album.Barcode },
        relationships = new { artists = new { data = new[] { new { id = album.Artist.Id, type = "artists" } } } },
    };

    private static object ArtistResource(Artist artist) => new { id = artist.Id, type = "artists", attributes = new { name = artist.Name } };
}
