using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Spotify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Grab bags (migration 0018) against a real database, with Spotify faked.</summary>
[Collection(PostgresCollection.Name)]
public sealed class GrabBagApiTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private const string GeeseId = "066X20Nz7iquqkkCW6Jxy6";
    private const string NewArtistId = "0bbbbbbbbbbbbbbbbbbbbb";
    private const string AnotherArtistId = "0cccccccccccccccccccc1";
    private const string Track1 = "1aaaaaaaaaaaaaaaaaaaaa";
    private const string Track2 = "2aaaaaaaaaaaaaaaaaaaaa";
    private const string Track3 = "3aaaaaaaaaaaaaaaaaaaaa";

    private readonly FakeSpotify _spotify = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"grab_bag_api_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        _spotify.Artists.Add(new FakeSpotify.Artist(AnotherArtistId, "Another Band"));
        _spotify.Tracks.Add(new FakeSpotify.TrackDetail(Track1, "Cowboy Nudes", "al-1", "3D Country", (GeeseId, "Geese")));
        _spotify.Tracks.Add(new FakeSpotify.TrackDetail(Track2, "2122", "al-1", "3D Country", (GeeseId, "Geese")));
        _spotify.Tracks.Add(new FakeSpotify.TrackDetail(Track3, "Brand New Song", "al-2", "Debut", (NewArtistId, "Newcomer"), (GeeseId, "Geese")));

        var options = FakeSpotify.Options();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
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
        await ScoringSetup.ActiveArtistAsync(_db, GeeseId, "Geese", releases: 1);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public void Dispose() => _spotify.Dispose();

    [Fact]
    public async Task Songs_fill_numbered_slots_move_between_them_and_track_new_artists_outside_the_daily_limit()
    {
        var ana = await SignInAsync("ana");
        await using (var connection = await _db.OpenConnectionAsync())
        {
            // Ana is at today's limit of hand-added artists.
            await connection.ExecuteAsync(
                """
                INSERT INTO artists (name, spotify_artist_id, added_by)
                SELECT 'Bulk ' || n, 'bulk-' || n, u.id FROM users u, generate_series(1, 50) n WHERE u.handle = 'ana'
                """);
        }

        var bag = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/1", ana, new { spotify_track_id = Track1 }), HttpStatusCode.OK);
        var song = Assert.Single(bag.GetProperty("items").EnumerateArray());
        Assert.Equal((1, "Cowboy Nudes", "Geese", "active", "3D Country"), (song.GetProperty("position").GetInt32(),
            song.GetProperty("title").GetString(), song.GetProperty("artist").GetProperty("name").GetString(),
            song.GetProperty("artist_status").GetString(), song.GetProperty("album_title").GetString()));
        Assert.Equal($"https://open.spotify.com/track/{Track1}", song.GetProperty("spotify_url").GetString());
        Assert.Equal("https://i.scdn.co/al-1-640", song.GetProperty("cover_url").GetString());
        Assert.Equal(5, bag.GetProperty("slots").GetInt32());

        // A song by an artist nobody tracks: the artist is tracked (onboarding) although Ana is at the daily limit.
        bag = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/3", ana, new { spotify_track_id = Track3 }), HttpStatusCode.OK);
        var newSong = bag.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("position").GetInt32() == 3);
        Assert.Equal(("Newcomer", "onboarding"), (newSong.GetProperty("artist").GetProperty("name").GetString(), newSong.GetProperty("artist_status").GetString()));
        await using (var connection = await _db.OpenConnectionAsync())
        {
            Assert.Equal("grab_bag", await connection.ExecuteScalarAsync<string>(
                "SELECT added_via FROM artists WHERE spotify_artist_id = @NewArtistId", new { NewArtistId }));
            Assert.True(await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM jobs WHERE kind = 'artist.onboard' AND payload->>'artist_id' = (SELECT id::text FROM artists WHERE spotify_artist_id = @NewArtistId))",
                new { NewArtistId }));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(HttpMethod.Post, "/v1/artists", ana,
            new { spotify_artist_id = AnotherArtistId })).StatusCode);

        // The same song put in another slot moves there; a slot can be emptied.
        bag = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/2", ana, new { spotify_track_id = Track1 }), HttpStatusCode.OK);
        Assert.Equal([2, 3], bag.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("position").GetInt32()));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/v1/me/grab-bag/3", ana)).StatusCode);
        bag = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/grab-bag", ana), HttpStatusCode.OK);
        Assert.Equal([2], bag.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("position").GetInt32()));
        Assert.Equal("ana", bag.GetProperty("owner").GetProperty("handle").GetString());

        // Validation: slots are 1 to 5, ids are Spotify ids, and Spotify must know the track.
        var slot = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/6", ana, new { spotify_track_id = Track2 }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("position", slot.GetProperty("errors")[0].GetProperty("field").GetString());
        var id = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/1", ana, new { spotify_track_id = "nope" }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("spotify_track_id", id.GetProperty("errors")[0].GetProperty("field").GetString());
        var unknown = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/1", ana, new { spotify_track_id = "9zzzzzzzzzzzzzzzzzzzzz" }), HttpStatusCode.NotFound);
        Assert.Equal("spotify_track_not_found", unknown.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Songs_can_be_reordered_across_the_five_slots()
    {
        var ana = await SignInAsync("ana");
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/1", ana, new { spotify_track_id = Track1 }), HttpStatusCode.OK);
        var bag = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/3", ana, new { spotify_track_id = Track2 }), HttpStatusCode.OK);
        var ids = bag.GetProperty("items").EnumerateArray().ToDictionary(s => s.GetProperty("title").GetString()!, s => s.GetProperty("id").GetString());

        var reordered = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/order", ana,
            new { slots = new[] { ids["2122"], null, null, null, ids["Cowboy Nudes"] } }), HttpStatusCode.OK);
        Assert.Equal([("2122", 1), ("Cowboy Nudes", 5)], reordered.GetProperty("items").EnumerateArray()
            .Select(s => (s.GetProperty("title").GetString(), s.GetProperty("position").GetInt32())));
        Assert.Equal(ids["2122"], reordered.GetProperty("items")[0].GetProperty("id").GetString());   // same song rows

        foreach (var slots in new[]
                 {
                     new[] { ids["2122"], null, null, null },                            // four slots
                     new[] { ids["2122"], null, null, null, null },                      // a song missing
                     new[] { ids["2122"], ids["2122"], null, null, ids["Cowboy Nudes"] }, // a song twice
                     new[] { ids["2122"], Guid.NewGuid().ToString(), null, null, ids["Cowboy Nudes"] }, // not hers
                 })
        {
            var refused = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/order", ana, new { slots }), HttpStatusCode.UnprocessableEntity);
            Assert.Equal("slots", refused.GetProperty("errors")[0].GetProperty("field").GetString());
        }
    }

    [Fact]
    public async Task Followers_and_the_owner_see_a_grab_bag_and_nobody_else_does()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var cat = await SignInAsync("cat");
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/1", ana, new { spotify_track_id = Track2 }), HttpStatusCode.OK);
        await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben);

        var seen = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/grab-bag", ben), HttpStatusCode.OK);
        Assert.Equal("2122", Assert.Single(seen.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        var refused = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/grab-bag", cat), HttpStatusCode.Forbidden);
        Assert.Equal("followers_only", refused.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/v1/users/ana/grab-bag", ana)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/v1/users/nobody/grab-bag", ana)).StatusCode);
        Assert.Equal(1, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana", ben), HttpStatusCode.OK)).GetProperty("grab_bag_count").GetInt32());
    }

    [Fact]
    public async Task The_bouncer_door_draws_a_song_from_followed_fans_grab_bags()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/1", ana, new { spotify_track_id = Track1 }), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/grab-bag/2", ana, new { spotify_track_id = Track2 }), HttpStatusCode.OK);

        var nobody = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/bouncer-door", ben), HttpStatusCode.OK);
        Assert.Equal((JsonValueKind.Null, 0, 0), (nobody.GetProperty("song").ValueKind, nobody.GetProperty("following").GetInt32(),
            nobody.GetProperty("songs").GetInt32()));

        await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben);
        var titles = new HashSet<string>();
        for (var i = 0; i < 12; i++)
        {
            var draw = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/bouncer-door", ben), HttpStatusCode.OK);
            Assert.Equal(("ana", 1, 2), (draw.GetProperty("owner").GetProperty("handle").GetString(), draw.GetProperty("following").GetInt32(),
                draw.GetProperty("songs").GetInt32()));
            titles.Add(draw.GetProperty("song").GetProperty("title").GetString()!);
        }

        Assert.Subset(new HashSet<string> { "Cowboy Nudes", "2122" }, titles);
        // Ana's own bag is not her bouncer door.
        var own = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/bouncer-door", ana), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, own.GetProperty("song").ValueKind);
    }

    [Fact]
    public async Task Song_search_asks_Spotify_and_marks_tracked_artists()
    {
        var ana = await SignInAsync("ana");

        var results = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/tracks/search?q=brand", ana), HttpStatusCode.OK);
        var track = Assert.Single(results.GetProperty("items").EnumerateArray());
        Assert.Equal((Track3, "Brand New Song", "Debut"), (track.GetProperty("spotify_track_id").GetString(), track.GetProperty("title").GetString(),
            track.GetProperty("album_title").GetString()));
        var artists = track.GetProperty("artists").EnumerateArray().ToList();
        Assert.Equal(("Newcomer", JsonValueKind.Null), (artists[0].GetProperty("name").GetString(), artists[0].GetProperty("artist_id").ValueKind));
        Assert.Equal(("Geese", JsonValueKind.String), (artists[1].GetProperty("name").GetString(), artists[1].GetProperty("artist_id").ValueKind));
        Assert.Contains(_spotify.Calls, call => call.Path == "search" && call.Query.Contains("type=track", StringComparison.Ordinal));

        var tooShort = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/tracks/search?q=b", ana), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("q", tooShort.GetProperty("errors")[0].GetProperty("field").GetString());
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
