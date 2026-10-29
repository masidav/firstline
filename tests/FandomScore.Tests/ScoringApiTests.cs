using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>/me/scores, /artists/{id}/score (and refresh), and /admin/scoring-rules against a real database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ScoringApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"scoring_api_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task A_score_goes_from_onboarding_to_computing_to_ready()
    {
        var (token, handle) = await SignInAsync("fan_one");
        Guid artistId;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            artistId = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO artists (name, spotify_artist_id) VALUES ('Geese', 'sp-geese') RETURNING id");
        }

        var onboarding = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{artistId}/score", token), HttpStatusCode.OK);
        Assert.Equal("artist_onboarding", onboarding.GetProperty("status").GetString());
        var notActive = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/artists/{artistId}/score/refresh", token),
            HttpStatusCode.Conflict);
        Assert.Equal("artist_not_active", notActive.GetProperty("code").GetString());

        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("DELETE FROM artists WHERE id = @artistId", new { artistId });
        }

        artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 2);
        await ScoringSetup.FanAsync(_db, handle, artistId, liked: 100, playlist: 0, albums: 2, rank: 1, recent: 0);

        var computing = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/artists/{artistId}/score/refresh", token),
            HttpStatusCode.Accepted);
        Assert.Equal("computing", computing.GetProperty("status").GetString());
        Assert.True(await ScoringSetup.RunQueuedAsync(_db));

        var ready = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{artistId}/score", token), HttpStatusCode.OK);
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        // Liked tracks, album depth, and top artist are full: 3 of the 18 even signals.
        Assert.Equal(Math.Round(100.0 * 3 / 18, 2), (double)ready.GetProperty("raw_score").GetDecimal(), 2);
        Assert.True(ready.GetProperty("is_estimated").GetBoolean());
        Assert.Equal(1m, ready.GetProperty("breakdown").GetProperty("spotify").GetProperty("shares").GetProperty("top_artist").GetDecimal());

        var listed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/artists?q=geese", token), HttpStatusCode.OK);
        var myScore = Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("my_score");
        Assert.Equal(ready.GetProperty("percentile").GetDecimal(), myScore.GetProperty("percentile").GetDecimal());

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/scores", token), HttpStatusCode.OK);
        var item = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal("Geese", item.GetProperty("artist").GetProperty("name").GetString());
        Assert.Equal(ready.GetProperty("percentile").GetDecimal(), item.GetProperty("percentile").GetDecimal());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task My_scores_are_mine_only_sorted_by_percentile_and_paged()
    {
        var (token, handle) = await SignInAsync("fan_one");
        var (_, other) = await SignInAsync("fan_two");
        var big = await ScoringSetup.ActiveArtistAsync(_db, "sp-big", "Big Love", releases: 1);
        var small = await ScoringSetup.ActiveArtistAsync(_db, "sp-small", "Small Fry", releases: 1);
        await ScoringSetup.FanAsync(_db, handle, big, liked: 100, playlist: 100, albums: 1, rank: 1, recent: 50);
        await ScoringSetup.FanAsync(_db, handle, small, liked: 1, playlist: 0, albums: 0, rank: null, recent: 0);
        await ScoringSetup.FanAsync(_db, other, small, liked: 100, playlist: 100, albums: 1, rank: 1, recent: 50);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("SELECT request_full_rescore()");
        }

        Assert.True(await ScoringSetup.RunQueuedAsync(_db));
        Assert.True(await ScoringSetup.RunQueuedAsync(_db));

        var first = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/scores?limit=1", token), HttpStatusCode.OK);
        Assert.Equal("Big Love", Assert.Single(first.GetProperty("items").EnumerateArray()).GetProperty("artist").GetProperty("name").GetString());
        var cursor = Uri.EscapeDataString(first.GetProperty("next_cursor").GetString()!);
        var second = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/me/scores?limit=1&cursor={cursor}", token), HttpStatusCode.OK);
        Assert.Equal("Small Fry", Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("artist").GetProperty("name").GetString());

        var bad = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/scores?sort=loudest", token), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("sort", bad.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task An_unknown_artist_has_no_score()
    {
        var (token, _) = await SignInAsync("fan_one");
        var problem = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{Guid.NewGuid()}/score", token), HttpStatusCode.NotFound);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Staff_can_read_and_change_weights_and_a_change_queues_rescores()
    {
        var (token, handle) = await SignInAsync("staffer");
        var artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/v1/admin/scoring-rules", token)).StatusCode);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = @handle; DELETE FROM jobs;", new { handle });
        }

        var rules = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/scoring-rules", token), HttpStatusCode.OK);
        var spotify = rules.GetProperty("items").EnumerateArray()
            .Where(r => r.GetProperty("rule_key").GetString()!.StartsWith("spotify_", StringComparison.Ordinal)).ToList();
        Assert.Equal(18, spotify.Count);
        Assert.All(spotify, r => Assert.Equal(1m, r.GetProperty("points").GetDecimal()));
        Assert.All(spotify, r => Assert.True(r.GetProperty("saturation").GetDecimal() > 0));

        var updated = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/admin/scoring-rules/spotify_recent_play", token,
            new { points = 2.5, saturation = 25 }), HttpStatusCode.OK);
        Assert.Equal(2.5m, updated.GetProperty("points").GetDecimal());
        Assert.Equal(25m, updated.GetProperty("saturation").GetDecimal());
        Assert.True(updated.GetProperty("active").GetBoolean());
        await using (var connection = await _db.OpenConnectionAsync())
        {
            Assert.Equal(artistId.ToString(), await connection.ExecuteScalarAsync<string>(
                "SELECT payload->>'artist_id' FROM jobs WHERE kind = 'scoring.score_artist' AND status = 'queued'"));
        }

        var capRemoved = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/admin/scoring-rules/comment_posted", token,
            new { daily_cap_count = (int?)null }), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, capRemoved.GetProperty("daily_cap_count").ValueKind);
        Assert.Equal(1m, capRemoved.GetProperty("points").GetDecimal());
    }

    [Theory]
    [InlineData("spotify_liked_track", """{"points": -1}""", "points")]
    [InlineData("spotify_liked_track", """{"saturation": 0}""", "saturation")]
    [InlineData("spotify_liked_track", """{"active": "yes"}""", "active")]
    [InlineData("comment_posted", """{"saturation": 10}""", "saturation")]
    [InlineData("comment_posted", """{"daily_cap_count": 0}""", "daily_cap_count")]
    public async Task Rule_updates_are_validated(string ruleKey, string body, string field)
    {
        var (token, handle) = await SignInAsync("staffer");
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = @handle", new { handle });
        }

        var response = await SendAsync(HttpMethod.Put, $"/v1/admin/scoring-rules/{ruleKey}", token,
            content: new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity);

        Assert.Equal(field, problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Updating_an_unknown_rule_is_not_found()
    {
        var (token, handle) = await SignInAsync("staffer");
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = @handle", new { handle });
        }

        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/admin/scoring-rules/spotify_vibes", token, new { points = 1 }),
            HttpStatusCode.NotFound);
    }

    private async Task<(string Token, string Handle)> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return ((await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!, handle);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string token, object? body = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content ?? (body is null ? null : JsonContent.Create(body)) };
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
