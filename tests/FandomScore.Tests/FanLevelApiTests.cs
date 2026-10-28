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

/// <summary>Fan levels (migration 0039): the caller's own level with its parts, and levels on profiles and the feed for followers.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FanLevelApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"fan-level-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"fan_level_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Storage:LocalPath", _storage);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, recursive: true);
        }
    }

    [Fact]
    public async Task A_fan_sees_their_level_and_parts_and_only_followers_see_it_elsewhere()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var cara = await SignInAsync("cara");
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                SELECT id, @_artistId, 80, 100, false, 0.1 FROM users WHERE handle = 'ana';
                INSERT INTO follows (follower_id, followee_id)
                SELECT b.id, a.id FROM users a, users b WHERE a.handle = 'ana' AND b.handle = 'ben';
                INSERT INTO album_logs (user_id, album_id, visibility)
                SELECT u.id, al.id, 'public' FROM users u, albums al WHERE u.handle = 'ana' AND al.artist_id = @_artistId;
                INSERT INTO submissions (user_id, artist_id, kind)
                SELECT u.id, @_artistId, 'merch' FROM users u, generate_series(1, 5) WHERE u.handle = 'ana';
                UPDATE submissions SET status = 'verified';
                """,
                new { _artistId });
        }

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/level", ana), HttpStatusCode.OK);
        Assert.Equal((5, 2), (mine.GetProperty("max_level").GetInt32(), mine.GetProperty("level").GetInt32()));
        // fandom 100/300, albums ln 2 / ln 51, proof 5/5, trivia 0, prompts 0: average 0.30, level 2.
        var parts = mine.GetProperty("parts").EnumerateArray().ToDictionary(p => p.GetProperty("key").GetString()!, p => p.GetProperty("value").GetDecimal());
        Assert.Equal(["albums_logged", "fandom", "prompts", "trivia", "verified_proof"], parts.Keys.Order());
        Assert.Equal((1m, 0m), (parts["verified_proof"], parts["trivia"]));
        Assert.Equal(0.4m, mine.GetProperty("next_level_at").GetDecimal());

        // Stored levels come from the hourly refresh; then followers see Ana's on her page and her feed posts.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("SELECT refresh_fan_overall_scores()");
        }

        Assert.Equal(2, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana", ben), HttpStatusCode.OK)).GetProperty("fan_level").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana", cara), HttpStatusCode.OK)).GetProperty("fan_level").ValueKind);

        static IEnumerable<JsonElement> AnaItems(JsonElement feed) =>
            feed.GetProperty("items").EnumerateArray().Where(i => i.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                && u.GetProperty("handle").GetString() == "ana");
        var benFeed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed?limit=100", ben), HttpStatusCode.OK);
        Assert.NotEmpty(AnaItems(benFeed));
        Assert.All(AnaItems(benFeed), i => Assert.Equal(2, i.GetProperty("user_level").GetInt32()));
        var caraFeed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed?limit=100", cara), HttpStatusCode.OK);
        Assert.All(AnaItems(caraFeed), i => Assert.True(!i.TryGetProperty("user_level", out var l) || l.ValueKind == JsonValueKind.Null));
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
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
