using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>GET /artists, /artists/search, and POST /artists against a real database, with Spotify faked.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ArtistApiTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private const string ClientSecret = "test-client-secret";
    private const string GeeseId = "066X20Nz7iquqkkCW6Jxy6";
    private const string GeeseSoundalikeId = "0aaaaaaaaaaaaaaaaaaaaa";

    private readonly ConcurrentQueue<string> _logs = new();
    private readonly FakeSpotify _spotify = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"artist_api_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        _spotify.Artists.Add(new FakeSpotify.Artist(GeeseId, "Geese", "indie rock"));
        _spotify.Artists.Add(new FakeSpotify.Artist(GeeseSoundalikeId, "Geese Tribute Band"));

        var options = FakeSpotify.Options();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Spotify:ClientId", options.ClientId);
            builder.UseSetting("Spotify:ClientSecret", ClientSecret);
            builder.UseSetting("Spotify:RedirectUris", options.RedirectUris);
            builder.UseSetting("Spotify:TokenKey", options.TokenKey);
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_logs)));
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>());
                services.AddHttpClient<SpotifyClient>().ConfigurePrimaryHttpMessageHandler(() => _spotify);
            });
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public void Dispose() => _spotify.Dispose();

    [Fact]
    public async Task Search_uses_an_app_token_and_marks_tracked_artists_without_storing_the_rest()
    {
        var token = await SignInAsync("fan_one");
        await JsonAsync(await AddAsync(token, GeeseId), HttpStatusCode.Created);

        var results = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/artists/search?q=geese&limit=5", token), HttpStatusCode.OK);

        var items = results.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var geese = items.Single(i => i.GetProperty("spotify_artist_id").GetString() == GeeseId);
        Assert.Equal("Geese", geese.GetProperty("name").GetString());
        Assert.True(geese.GetProperty("tracked").GetBoolean());
        Assert.Equal("onboarding", geese.GetProperty("status").GetString());
        Assert.Equal($"https://i.scdn.co/{GeeseId}-320", geese.GetProperty("image_url").GetString());
        Assert.Equal(["indie rock"], geese.GetProperty("genres").EnumerateArray().Select(g => g.GetString()));
        var tribute = items.Single(i => i.GetProperty("spotify_artist_id").GetString() == GeeseSoundalikeId);
        Assert.False(tribute.GetProperty("tracked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tribute.GetProperty("artist_id").ValueKind);

        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM artists"));

        // One app token (client credentials, Basic auth with the client secret), reused for every call.
        var tokenCalls = _spotify.Calls.Where(c => c.Path == "token").ToList();
        var tokenCall = Assert.Single(tokenCalls);
        Assert.Equal("client_credentials", tokenCall.Form["grant_type"]);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{FakeSpotify.ClientId}:{ClientSecret}")), tokenCall.Bearer);
        var search = Assert.Single(_spotify.Calls, c => c.Path == "search");
        Assert.Equal("app-1", search.Bearer);
        Assert.Contains("limit=5", search.Query, StringComparison.Ordinal);
        Assert.Contains("type=artist", search.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_tells_same_named_artists_apart_by_release_count()
    {
        const string impostorId = "0bbbbbbbbbbbbbbbbbbbbb";
        _spotify.Artists.Insert(0, new FakeSpotify.Artist(impostorId, "GEESE"));
        _spotify.ArtistAlbums[impostorId] = [new("i-1", "Covers")];
        _spotify.ArtistAlbums[GeeseId] = [new("g-1", "3D Country"), new("g-2", "Getting Killed"), new("g-3", "Tomorrow's Fire", "single")];
        var token = await SignInAsync("fan_one");

        var results = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/artists/search?q=geese", token), HttpStatusCode.OK);

        var items = results.GetProperty("items").EnumerateArray()
            .Select(i => (Id: i.GetProperty("spotify_artist_id").GetString(), Count: i.GetProperty("release_count")))
            .ToList();
        Assert.Equal([GeeseId, impostorId, GeeseSoundalikeId], items.Select(i => i.Id));
        Assert.Equal(3, items[0].Count.GetInt32());
        Assert.Equal(1, items[1].Count.GetInt32());
        Assert.Equal(JsonValueKind.Null, items[2].Count.ValueKind);
        // Only the two same-named artists were counted, each with one small request.
        var counts = _spotify.Calls.Where(c => c.Path.EndsWith("/albums", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, counts.Count);
        Assert.All(counts, c => Assert.Contains("limit=1", c.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_release_count_still_returns_the_search()
    {
        const string impostorId = "0bbbbbbbbbbbbbbbbbbbbb";
        _spotify.Artists.Add(new FakeSpotify.Artist(impostorId, "Geese"));
        _spotify.ArtistAlbums[GeeseId] = [new("g-1", "3D Country")];
        var token = await SignInAsync("fan_one");

        var results = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/artists/search?q=geese", token), HttpStatusCode.OK);

        var items = results.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(GeeseId, items[0].GetProperty("spotify_artist_id").GetString());
        Assert.Equal(1, items[0].GetProperty("release_count").GetInt32());
        // Spotify answers 404 for the impostor's albums here.
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("release_count").ValueKind);
    }

    [Theory]
    [InlineData("q=g", "q")]
    [InlineData("q=geese&limit=11", "limit")]
    [InlineData("q=geese&limit=0", "limit")]
    public async Task Search_rejects_bad_parameters(string query, string field)
    {
        var token = await SignInAsync("fan_one");

        var problem = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/search?{query}", token),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.Equal(field, problem.GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Empty(_spotify.Calls);
    }

    [Fact]
    public async Task Search_needs_sign_in()
    {
        var response = await _client.GetAsync("/v1/artists/search?q=geese");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_client_secret_search_and_add_are_unavailable()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("Spotify:ClientSecret", ""));
        var client = factory.CreateClient();
        var token = await SignInAsync("fan_one", client);

        var search = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/artists/search?q=geese", token, client: client),
            HttpStatusCode.ServiceUnavailable);
        var add = await JsonAsync(await AddAsync(token, GeeseId, client), HttpStatusCode.ServiceUnavailable);

        Assert.Equal("spotify_unavailable", search.GetProperty("code").GetString());
        Assert.Equal("spotify_unavailable", add.GetProperty("code").GetString());
        Assert.Empty(_spotify.Calls);
    }

    [Fact]
    public async Task A_rejected_client_secret_is_unavailable_and_logged_without_the_secret()
    {
        _spotify.AppTokenStatus = HttpStatusCode.BadRequest;
        var token = await SignInAsync("fan_one");

        var problem = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/artists/search?q=geese", token),
            HttpStatusCode.ServiceUnavailable);

        Assert.Equal("spotify_unavailable", problem.GetProperty("code").GetString());
        Assert.Contains(_logs, line => line.Contains("invalid_client", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs, line => line.Contains(ClientSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adding_takes_name_and_image_from_Spotify_and_starts_onboarding()
    {
        var token = await SignInAsync("fan_one");

        var response = await AddAsync(token, GeeseId);
        var artist = await JsonAsync(response, HttpStatusCode.Created);

        Assert.Equal($"/v1/artists/{artist.GetProperty("id").GetString()}", response.Headers.Location?.ToString());
        Assert.Equal("Geese", artist.GetProperty("name").GetString());
        Assert.Equal(GeeseId, artist.GetProperty("spotify_artist_id").GetString());
        Assert.Equal($"https://i.scdn.co/{GeeseId}-320", artist.GetProperty("image_url").GetString());
        Assert.Equal("onboarding", artist.GetProperty("status").GetString());
        Assert.True(artist.GetProperty("added_by_me").GetBoolean());
        Assert.Equal(JsonValueKind.Null, artist.GetProperty("activated_at").ValueKind);
        Assert.Single(_spotify.Calls, c => c.Path == $"artists/{GeeseId}");
    }

    [Fact]
    public async Task Adding_a_tracked_artist_returns_it_without_calling_Spotify()
    {
        var first = await SignInAsync("fan_one");
        var second = await SignInAsync("fan_two");
        var added = await JsonAsync(await AddAsync(first, GeeseId), HttpStatusCode.Created);

        var again = await JsonAsync(await AddAsync(second, GeeseId), HttpStatusCode.OK);

        Assert.Equal(added.GetProperty("id").GetString(), again.GetProperty("id").GetString());
        Assert.False(again.GetProperty("added_by_me").GetBoolean());
        Assert.Single(_spotify.Calls, c => c.Path == $"artists/{GeeseId}");
    }

    [Fact]
    public async Task Adding_an_artist_Spotify_does_not_know_is_not_found()
    {
        var token = await SignInAsync("fan_one");

        var problem = await JsonAsync(await AddAsync(token, "1zzzzzzzzzzzzzzzzzzzzz"), HttpStatusCode.NotFound);

        Assert.Equal("spotify_artist_not_found", problem.GetProperty("code").GetString());
        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM artists"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("geese")]
    [InlineData("066X20Nz7iquqkkCW6Jxy6/../me")]
    public async Task Adding_needs_a_Spotify_artist_id(string? spotifyArtistId)
    {
        var token = await SignInAsync("fan_one");

        var problem = await JsonAsync(await AddAsync(token, spotifyArtistId), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("spotify_artist_id", problem.GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Empty(_spotify.Calls);
    }

    [Fact]
    public async Task The_daily_add_limit_returns_artist_add_limit()
    {
        var token = await SignInAsync("fan_one");
        var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", token), HttpStatusCode.OK);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO artists (name, spotify_artist_id, added_by) SELECT 'Artist ' || n, 'sp-' || n, @userId FROM generate_series(1, 50) n",
                new { userId = Guid.Parse(me.GetProperty("id").GetString()!) });
        }

        var response = await AddAsync(token, GeeseId);
        var problem = await JsonAsync(response, HttpStatusCode.TooManyRequests);

        Assert.Equal("artist_add_limit", problem.GetProperty("code").GetString());
        var retryAfter = int.Parse(response.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 24 * 60 * 60);
    }

    [Fact]
    public async Task Search_is_rate_limited_per_user()
    {
        var token = await SignInAsync("fan_one");
        for (var i = 0; i < 30; i++)
        {
            await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/artists/search?q=geese", token), HttpStatusCode.OK);
        }

        var limited = await SendAsync(HttpMethod.Get, "/v1/artists/search?q=geese", token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        var other = await SignInAsync("fan_two");
        await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/artists/search?q=geese", other), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Listing_matches_word_prefixes_and_filters_by_status_without_calling_Spotify()
    {
        var token = await SignInAsync("fan_one");
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO artists (name, spotify_artist_id) VALUES
                  ('The Beatles', 'sp-beatles'), ('Beach House', 'sp-beach'), ('Beabadoobee', 'sp-bea'),
                  ('Lobeat', 'sp-lobeat'), ('100%_Real', 'sp-percent');
                -- Activation needs a baseline.
                INSERT INTO artist_baselines (artist_id, dist_params, curve)
                  SELECT id, '{}', '[]' FROM artists WHERE spotify_artist_id IN ('sp-beatles', 'sp-lobeat', 'sp-percent');
                UPDATE artists SET status = 'active', activated_at = now(), catalog_loaded_at = now()
                  WHERE spotify_artist_id IN ('sp-beatles', 'sp-lobeat', 'sp-percent');
                UPDATE artists SET status = 'paused' WHERE spotify_artist_id = 'sp-bea';
                """);
        }

        async Task<List<string?>> NamesAsync(string query) =>
            (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists?{query}", token), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();

        Assert.Equal(["The Beatles"], await NamesAsync("q=bea"));
        Assert.Equal(["Beach House", "The Beatles"], await NamesAsync("q=BEA&status=all&sort=name"));
        Assert.Equal(["Beach House"], await NamesAsync("q=bea&status=onboarding"));
        Assert.Equal(["100%_Real"], await NamesAsync("q=100%25_&status=all"));
        Assert.Empty(await NamesAsync("q=1%25"));
        Assert.Empty(_spotify.Calls);
    }

    [Fact]
    public async Task Listing_can_be_limited_to_artists_the_fan_added()
    {
        var token = await SignInAsync("fan_one");
        await JsonAsync(await AddAsync(token, GeeseId), HttpStatusCode.Created);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("INSERT INTO artists (name, spotify_artist_id) VALUES ('Someone Else''s Pick', 'sp-other')");
        }

        async Task<List<string?>> NamesAsync(string query) =>
            (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists?{query}", token), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();

        Assert.Equal(["Geese"], await NamesAsync("status=onboarding&added_by_me=true"));
        Assert.Equal(["Geese", "Someone Else's Pick"], await NamesAsync("status=onboarding&sort=name"));
        Assert.Equal(["Geese", "Someone Else's Pick"], await NamesAsync("status=onboarding&sort=name&added_by_me=false"));
    }

    [Fact]
    public async Task Listing_pages_with_a_cursor()
    {
        var token = await SignInAsync("fan_one");
        await JsonAsync(await AddAsync(token, GeeseId), HttpStatusCode.Created);
        await JsonAsync(await AddAsync(token, GeeseSoundalikeId), HttpStatusCode.Created);

        var first = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/artists?status=all&sort=name&limit=1", token),
            HttpStatusCode.OK);
        var cursor = first.GetProperty("next_cursor").GetString();
        var second = await JsonAsync(
            await SendAsync(HttpMethod.Get, $"/v1/artists?status=all&sort=name&limit=1&cursor={Uri.EscapeDataString(cursor!)}", token),
            HttpStatusCode.OK);

        var geese = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal("Geese", geese.GetProperty("name").GetString());
        Assert.True(geese.GetProperty("added_by_me").GetBoolean());
        Assert.Equal("Geese Tribute Band", Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next_cursor").ValueKind);
    }

    [Theory]
    [InlineData("status=paused", "status")]
    [InlineData("sort=popular", "sort")]
    [InlineData("limit=101", "limit")]
    [InlineData("cursor=not-a-cursor", "cursor")]
    public async Task Listing_rejects_bad_parameters(string query, string field)
    {
        var token = await SignInAsync("fan_one");

        var problem = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists?{query}", token),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal(field, problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Listing_needs_sign_in()
    {
        var response = await _client.GetAsync("/v1/artists");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> SignInAsync(string handle, HttpClient? client = null)
    {
        var response = await (client ?? _client).PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> AddAsync(string token, string? spotifyArtistId, HttpClient? client = null) =>
        SendAsync(HttpMethod.Post, "/v1/artists", token, JsonContent.Create(new { spotify_artist_id = spotifyArtistId }), client);

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string token, HttpContent? content = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client ?? _client).SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
