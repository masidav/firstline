using FandomScore.Tests.Contract;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Featured;
using FandomScore.Database;
using FandomScore.Spotify;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Featured artist of the week (migration 0023): staff pick, video link or upload, Spotify song; fans read it.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FeaturedApiTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private const string Track = "1aaaaaaaaaaaaaaaaaaaaa";

    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"featured-tests-{Guid.NewGuid():N}");
    private readonly FakeSpotify _spotify = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    private static string ThisMonday
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            return today.AddDays(-(((int)today.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"featured_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _spotify.Tracks.Add(new FakeSpotify.TrackDetail(Track, "Trinidad", "al-1", "Getting Killed", ("sp-geese", "Geese")));
        var options = FakeSpotify.Options();
        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Storage:LocalPath", _storage);
            builder.UseSetting("Spotify:ClientId", options.ClientId);
            builder.UseSetting("Spotify:ClientSecret", "test-client-secret");
            builder.UseSetting("Spotify:RedirectUris", options.RedirectUris);
            builder.UseSetting("Spotify:TokenKey", options.TokenKey);
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>());
                services.AddHttpClient<SpotifyClient>().ConfigurePrimaryHttpMessageHandler(() => _spotify);
            });
        });
        _client = _factory.CreateClient();
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public void Dispose() => _spotify.Dispose();

    [Fact]
    public async Task Staff_pick_the_week_with_a_youtube_link_and_a_spotify_song_and_fans_see_it()
    {
        var fan = await SignInAsync("fan");
        var staff = await StaffAsync("staffer");
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/featured", fan), HttpStatusCode.OK)).GetProperty("featured").ValueKind);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/v1/admin/featured/{ThisMonday}", fan,
            new { artist_id = _artistId, video_url = "https://youtu.be/dQw4w9WgXcQ", spotify_track = Track })).StatusCode);
        var pick = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/admin/featured/{ThisMonday}", staff, new
        {
            artist_id = _artistId, headline = "Geese are on tour", video_url = "https://youtu.be/dQw4w9WgXcQ",
            spotify_track = $"https://open.spotify.com/track/{Track}?si=abc",
        }), HttpStatusCode.OK);
        Assert.Equal(("youtube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/embed/dQw4w9WgXcQ"),
            (pick.GetProperty("video").GetProperty("kind").GetString(), pick.GetProperty("video").GetProperty("url").GetString(),
             pick.GetProperty("video").GetProperty("embed_url").GetString()));
        Assert.Equal(("Trinidad", "Getting Killed", $"https://open.spotify.com/track/{Track}"), (pick.GetProperty("song").GetProperty("title").GetString(),
            pick.GetProperty("song").GetProperty("album_title").GetString(), pick.GetProperty("song").GetProperty("spotify_url").GetString()));

        var current = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/featured", fan), HttpStatusCode.OK)).GetProperty("featured");
        Assert.Equal(("Geese", "Geese are on tour", ThisMonday), (current.GetProperty("artist").GetProperty("name").GetString(),
            current.GetProperty("headline").GetString(), current.GetProperty("week_start").GetString()));

        // Replacing the week's pick with a TikTok.
        var tiktok = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/admin/featured/{ThisMonday}", staff, new
        {
            artist_id = _artistId, video_url = "https://www.tiktok.com/@geese/video/7300000000000000001", spotify_track = Track,
        }), HttpStatusCode.OK);
        Assert.Equal(("tiktok", "https://www.tiktok.com/embed/v2/7300000000000000001"), (tiktok.GetProperty("video").GetProperty("kind").GetString(),
            tiktok.GetProperty("video").GetProperty("embed_url").GetString()));
        Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/featured", staff), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/admin/featured/{ThisMonday}", staff)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/featured", fan), HttpStatusCode.OK)).GetProperty("featured").ValueKind);
    }

    [Fact]
    public async Task Picks_are_validated()
    {
        var staff = await StaffAsync("staffer");
        var tuesday = DateOnly.ParseExact(ThisMonday, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal("week_start", await FieldAsync(tuesday, new { artist_id = _artistId, video_url = "https://youtu.be/dQw4w9WgXcQ", spotify_track = Track }));
        Assert.Equal("video_url", await FieldAsync(ThisMonday, new { artist_id = _artistId, video_url = "https://vimeo.com/123", spotify_track = Track }));
        Assert.Equal("video_url", await FieldAsync(ThisMonday, new { artist_id = _artistId, spotify_track = Track }));
        Assert.Equal("spotify_track", await FieldAsync(ThisMonday, new { artist_id = _artistId, video_url = "https://youtu.be/dQw4w9WgXcQ", spotify_track = "nope" }));
        Assert.Equal("artist_id", await FieldAsync(ThisMonday, new { artist_id = Guid.NewGuid(), video_url = "https://youtu.be/dQw4w9WgXcQ", spotify_track = Track }));
        Assert.Equal("video_upload_id", await FieldAsync(ThisMonday, new { artist_id = _artistId, video_upload_id = Guid.NewGuid(), spotify_track = Track }));
        var unknown = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/admin/featured/{ThisMonday}", staff,
            new { artist_id = _artistId, video_url = "https://youtu.be/dQw4w9WgXcQ", spotify_track = "9zzzzzzzzzzzzzzzzzzzzz" }), HttpStatusCode.NotFound);
        Assert.Equal("spotify_track_not_found", unknown.GetProperty("code").GetString());

        async Task<string> FieldAsync(string week, object body) =>
            (await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/admin/featured/{week}", staff, body), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors")[0].GetProperty("field").GetString()!;
    }

    [Fact]
    public async Task Staff_upload_an_mp4_that_fans_play_through_a_signed_link()
    {
        var fan = await SignInAsync("fan");
        var staff = await StaffAsync("staffer");
        var mp4 = new byte[] { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0, 1, 2, 3, 4 };

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/v1/uploads", fan,
            new { purpose = "featured_video", mime_type = "video/mp4", bytes = mp4.Length })).StatusCode);
        Assert.Equal("mime_type", (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", staff,
            new { purpose = "featured_video", mime_type = "image/png", bytes = 10 }), HttpStatusCode.UnprocessableEntity)).GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Equal("bytes", (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", staff,
            new { purpose = "featured_video", mime_type = "video/mp4", bytes = UploadLimit + 1 }), HttpStatusCode.UnprocessableEntity)).GetProperty("errors")[0].GetProperty("field").GetString());

        var notVideo = await UploadAsync(staff, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notVideo.Status);
        var (uploadId, status) = await UploadAsync(staff, mp4);
        Assert.Equal(HttpStatusCode.NoContent, status);

        var pick = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/admin/featured/{ThisMonday}", staff,
            new { artist_id = _artistId, video_upload_id = uploadId, spotify_track = Track }), HttpStatusCode.OK);
        Assert.Equal("upload", pick.GetProperty("video").GetProperty("kind").GetString());

        var url = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/featured", fan), HttpStatusCode.OK)).GetProperty("featured").GetProperty("video")
            .GetProperty("url").GetString()!;
        var video = await _client.GetAsync(new Uri(url).PathAndQuery);
        Assert.Equal((HttpStatusCode.OK, "video/mp4"), (video.StatusCode, video.Content.Headers.ContentType?.MediaType));
        Assert.Equal(mp4, await video.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync($"/v1/featured/videos/{uploadId}?exp=1&sig=bad")).StatusCode);
    }

    private const int UploadLimit = 100 * 1024 * 1024;

    private async Task<(string? Id, HttpStatusCode Status)> UploadAsync(string token, byte[] content)
    {
        var intent = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", token,
            new { purpose = "featured_video", mime_type = "video/mp4", bytes = content.Length }), HttpStatusCode.Created);
        var put = new HttpRequestMessage(HttpMethod.Put, new Uri(intent.GetProperty("url").GetString()!).PathAndQuery) { Content = new ByteArrayContent(content) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        var response = await _client.SendAsync(put);
        return (intent.GetProperty("upload_id").GetString(), response.StatusCode);
    }

    private async Task<string> StaffAsync(string handle)
    {
        var token = await SignInAsync(handle);
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = @handle", new { handle });
        return token;
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }
}

/// <summary>Video link and Spotify track parsing for featured picks.</summary>
public sealed class FeaturedLinkTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10", "youtube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "youtube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/shorts/dQw4w9WgXcQ", "youtube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.tiktok.com/@geese.band/video/7300000000000000001?lang=en", "tiktok", "https://www.tiktok.com/@geese.band/video/7300000000000000001")]
    [InlineData("https://vm.tiktok.com/ZMabc123/", "tiktok", "https://vm.tiktok.com/ZMabc123/")]
    public void Video_links_are_recognised_and_normalised(string link, string kind, string url) =>
        Assert.Equal((kind, url), FeaturedEndpoints.ParseVideoLink(link));

    [Theory]
    [InlineData("https://vimeo.com/123")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("ftp://youtu.be/dQw4w9WgXcQ")]
    [InlineData("not a link")]
    public void Other_links_are_refused(string link) => Assert.Null(FeaturedEndpoints.ParseVideoLink(link));

    [Theory]
    [InlineData("1aaaaaaaaaaaaaaaaaaaaa", "1aaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://open.spotify.com/track/1aaaaaaaaaaaaaaaaaaaaa?si=x", "1aaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://open.spotify.com/intl-de/track/1aaaaaaaaaaaaaaaaaaaaa", "1aaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://open.spotify.com/album/1aaaaaaaaaaaaaaaaaaaaa", null)]
    [InlineData(null, null)]
    public void Spotify_tracks_come_from_ids_or_links(string? value, string? expected) =>
        Assert.Equal(expected, FeaturedEndpoints.ParseSpotifyTrack(value));
}
