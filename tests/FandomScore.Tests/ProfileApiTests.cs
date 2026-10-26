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

/// <summary>Fan profiles: what a stranger, a follower, and the fan themself see on a fan's page.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProfileApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"profiles_{Guid.NewGuid():N}");
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
    public async Task Strangers_see_the_basics_followers_see_stats_and_the_fan_sees_everything()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var cat = await SignInAsync("cat");
        var artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                UPDATE users SET is_artist = true, band_url = 'https://geese.example.test', instagram_handle = 'anaband' WHERE handle = 'ana';
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                  SELECT id, @artistId, 70, 92, false, 0.1 FROM users WHERE handle = 'ana';
                INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks) SELECT id, @artistId, 40 FROM users WHERE handle = 'ana';
                INSERT INTO album_logs (user_id, album_id, rating, visibility)
                  SELECT u.id, al.id, 9, v.visibility::visibility FROM users u, albums al, (VALUES ('public'), ('followers')) v (visibility)
                   WHERE u.handle = 'ana' AND al.artist_id = @artistId;
                INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id)
                  SELECT id, 1, '1aaaaaaaaaaaaaaaaaaaaa', 'Cowboy Nudes', @artistId FROM users WHERE handle = 'ana';
                """,
                new { artistId });
        }

        var prompt = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/prompts/today", ana), HttpStatusCode.OK)).GetProperty("prompt").GetProperty("id").GetString();
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{prompt}/answer", ana, new { body = "The Velvet Ropes" }), HttpStatusCode.OK);

        // Ben follows Ana; Cat doesn't.
        await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben);

        var stranger = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana", cat), HttpStatusCode.OK);
        Assert.Equal((false, false, 1, 1, 1), (stranger.GetProperty("is_me").GetBoolean(), stranger.GetProperty("can_see_follower_content").GetBoolean(),
            stranger.GetProperty("log_count").GetInt32(), stranger.GetProperty("answer_count").GetInt32(), stranger.GetProperty("grab_bag_count").GetInt32()));
        Assert.Equal(("https://geese.example.test", "anaband"), (stranger.GetProperty("band_url").GetString(), stranger.GetProperty("instagram_handle").GetString()));
        Assert.Equal("followers_only", (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/stats", cat), HttpStatusCode.Forbidden)).GetProperty("code").GetString());
        Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/logs", cat), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());

        var follower = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana", ben), HttpStatusCode.OK);
        Assert.Equal((true, 2), (follower.GetProperty("can_see_follower_content").GetBoolean(), follower.GetProperty("log_count").GetInt32()));
        var stats = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/stats", ben), HttpStatusCode.OK);
        var geese = Assert.Single(stats.GetProperty("artists").EnumerateArray());
        Assert.Equal(("Geese", 92m, 1), (geese.GetProperty("artist").GetProperty("name").GetString(), geese.GetProperty("percentile").GetDecimal(),
            geese.GetProperty("rank").GetInt32()));
        Assert.Equal(2, stats.GetProperty("journal").GetProperty("logs").GetInt32());
        Assert.False(stats.TryGetProperty("listening", out _));   // listening stays private

        var answers = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/answers", cat), HttpStatusCode.OK);
        var answer = Assert.Single(answers.GetProperty("items").EnumerateArray());
        Assert.Equal("The Velvet Ropes", answer.GetProperty("answer").GetProperty("body").GetString());
        Assert.False(string.IsNullOrEmpty(answer.GetProperty("prompt_text").GetString()));

        var self = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana", ana), HttpStatusCode.OK);
        Assert.Equal((true, true), (self.GetProperty("is_me").GetBoolean(), self.GetProperty("can_see_follower_content").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/v1/users/ana/stats", ana)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/v1/users/nobody/stats", ana)).StatusCode);
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
