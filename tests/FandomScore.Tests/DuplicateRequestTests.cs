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

/// <summary>
/// The security pass (2026-10-28): the same request sent many times at once (a double tap, a retry, a script) makes one
/// row, never an error or a duplicate.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DuplicateRequestTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"duplicate-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"duplicate_{Guid.NewGuid():N}");
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
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 3);
        await using var catalog = await _db.OpenConnectionAsync();
        await catalog.ExecuteAsync(
            """
            UPDATE albums SET release_date = make_date(2018 + (right(spotify_album_id, 1))::int, 5, 1) WHERE artist_id = @_artistId;
            INSERT INTO tracks (album_id, title, track_number)
              SELECT al.id, 'Song ' || al.spotify_album_id || '-' || n, n FROM albums al, generate_series(1, 4) n WHERE al.artist_id = @_artistId;
            """,
            new { _artistId });
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

    private const int Copies = 8;

    [Fact]
    public async Task Follow_react_answer_register_a_device_and_report_at_once_each_leave_one_row()
    {
        await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        Guid logId;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            logId = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO album_logs (user_id, album_id, visibility)
                SELECT u.id, al.id, 'public' FROM users u, albums al WHERE u.handle = 'ana' AND al.artist_id = @_artistId LIMIT 1
                RETURNING id
                """,
                new { _artistId });
        }

        var promptId = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/prompts/today", ben), HttpStatusCode.OK))
            .GetProperty("prompt").GetProperty("id").GetString();

        await AllAtOnceAsync(() => SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben), HttpStatusCode.NoContent);
        await AllAtOnceAsync(() => SendAsync(HttpMethod.Put, $"/v1/reactions/log/{logId}/fire", ben), HttpStatusCode.OK);
        await AllAtOnceAsync(() => SendAsync(HttpMethod.Put, $"/v1/prompts/{promptId}/answer", ben, new { body = "The Velvet Ropes" }), HttpStatusCode.OK);
        await AllAtOnceAsync(() => SendAsync(HttpMethod.Put, "/v1/me/push-devices", ben, new { token = "ExponentPushToken[ben-phone-000001]", platform = "ios" }),
            HttpStatusCode.OK);
        await AllAtOnceAsync(() => SendAsync(HttpMethod.Post, "/v1/reports", ben, new { target_type = "log", target_id = logId, reason = "spam" }),
            HttpStatusCode.Accepted);

        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM follows"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM post_reactions"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM prompt_answers"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM push_devices"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM reports WHERE resolved_at IS NULL"));
    }

    [Fact]
    public async Task Opening_trivia_many_times_at_once_makes_one_question_and_answering_it_at_once_scores_once()
    {
        var fan = await SignInAsync("fan");
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                SELECT id, @_artistId, 50, 50, false, 0.1 FROM users WHERE handle = 'fan';
                """,
                new { _artistId });
        }

        var opened = await AllAtOnceAsync(() => SendAsync(HttpMethod.Get, "/v1/me/trivia", fan), HttpStatusCode.OK);
        var ids = opened.Select(o => o.GetProperty("question").GetProperty("id").GetString()).Distinct().ToList();
        var id = Assert.Single(ids);
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM trivia_questions"));
        var answerIndex = await ScalarAsync<int>("SELECT answer_index FROM trivia_questions");

        var answers = await AllAtOnceRawAsync(() => SendAsync(HttpMethod.Post, $"/v1/me/trivia/{id}/answer", fan, new { choice = answerIndex }));
        Assert.Single(answers, a => a == HttpStatusCode.OK);
        Assert.All(answers, a => Assert.True(a is HttpStatusCode.OK or HttpStatusCode.Conflict, $"answer: {(int)a}"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*) FROM score_events WHERE source_type = 'trivia' AND reversed_at IS NULL"));
    }

    /// <summary>Sends <see cref="Copies"/> copies at once; every answer must have the expected status.</summary>
    private static async Task<List<JsonElement>> AllAtOnceAsync(Func<Task<HttpResponseMessage>> send, HttpStatusCode expected)
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, Copies).Select(_ => send()));
        var bodies = new List<JsonElement>();
        foreach (var response in responses)
        {
            bodies.Add(await JsonAsync(response, expected));
        }

        return bodies;
    }

    private static async Task<List<HttpStatusCode>> AllAtOnceRawAsync(Func<Task<HttpResponseMessage>> send) =>
        [.. (await Task.WhenAll(Enumerable.Range(0, Copies).Select(_ => send()))).Select(r => r.StatusCode)];

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
