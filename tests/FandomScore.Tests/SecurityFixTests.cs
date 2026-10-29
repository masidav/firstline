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

/// <summary>Fixes from the security pass (2026-10-28): rate limits on costly calls, reaction removal visibility, and private counts.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SecurityFixTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"security-fix-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"security_fix_{Guid.NewGuid():N}");
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
    public async Task Rescores_and_album_searches_are_rate_limited_per_fan()
    {
        var fan = await SignInAsync("fan");
        var other = await SignInAsync("other");
        for (var i = 0; i < 10; i++)
        {
            var ok = await SendAsync(HttpMethod.Post, $"/v1/artists/{_artistId}/score/refresh", fan);
            Assert.True(ok.StatusCode != HttpStatusCode.TooManyRequests, $"rescore {i + 1} was limited");
        }

        var limited = await SendAsync(HttpMethod.Post, $"/v1/artists/{_artistId}/score/refresh", fan);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);
        // Another fan isn't affected.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SendAsync(HttpMethod.Post, $"/v1/artists/{_artistId}/score/refresh", other)).StatusCode);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
        {
            statuses.Add((await SendAsync(HttpMethod.Get, "/v1/albums/search?q=Album", fan)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(30));
    }

    [Fact]
    public async Task Removing_a_reaction_on_a_post_you_cant_see_is_not_found_and_others_counts_skip_private_logs()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        Guid privateLog;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            privateLog = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO album_logs (user_id, album_id, visibility, rating)
                SELECT u.id, al.id, 'private', 8 FROM users u, albums al WHERE u.handle = 'ana' AND al.artist_id = @_artistId LIMIT 1
                RETURNING id
                """,
                new { _artistId });
            await connection.ExecuteAsync(
                """
                INSERT INTO album_logs (user_id, album_id, visibility, rating)
                SELECT u.id, al.id, 'public', 7 FROM users u, albums al WHERE u.handle = 'ana' AND al.artist_id = @_artistId LIMIT 1;
                INSERT INTO follows (follower_id, followee_id) SELECT b.id, a.id FROM users a, users b WHERE a.handle = 'ana' AND b.handle = 'ben';
                """,
                new { _artistId });
        }

        await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/reactions/log/{privateLog}/fire", ben), HttpStatusCode.NotFound);
        await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/reactions/log/{privateLog}/fire", ana), HttpStatusCode.OK);

        // Ana's own stats count both logs; Ben (a follower) sees only the public one.
        var own = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/stats", ana), HttpStatusCode.OK);
        Assert.Equal((2, 2), (own.GetProperty("journal").GetProperty("logs").GetInt32(), own.GetProperty("journal").GetProperty("ratings").GetInt32()));
        var seen = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/stats", ben), HttpStatusCode.OK);
        Assert.Equal((1, 1), (seen.GetProperty("journal").GetProperty("logs").GetInt32(), seen.GetProperty("journal").GetProperty("ratings").GetInt32()));
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
