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

/// <summary>Emoji reactions on feed posts (migration 0026): add, take back, counts on the feed, visibility.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReactionApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"reactions_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
    public async Task Fans_react_with_several_emoji_take_them_back_and_the_feed_shows_counts()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var cat = await SignInAsync("cat");
        var (publicLog, followersLog) = await LogsAsync("ben");

        var first = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/reactions/log/{publicLog}/fire", ana), HttpStatusCode.OK);
        Assert.Equal(("fire", 1, true), Single(first));
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/reactions/log/{publicLog}/fire", ana), HttpStatusCode.OK);   // same one: still one
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/reactions/log/{publicLog}/heart", ana), HttpStatusCode.OK);
        var both = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/reactions/log/{publicLog}/fire", cat), HttpStatusCode.OK);
        Assert.Equal([("fire", 2, true), ("heart", 1, false)], Counts(both));   // cat's view: fire is hers too

        // Ana's feed: the log with fire 2 (hers) and heart 1 (hers), in the fixed order.
        var log = await FeedLogAsync(ana, publicLog);
        Assert.Equal([("fire", 2, true), ("heart", 1, true)], Counts(log));

        // Taking one back; doing it again is harmless.
        await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/reactions/log/{publicLog}/heart", ana), HttpStatusCode.OK);
        var after = await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/reactions/log/{publicLog}/heart", ana), HttpStatusCode.OK);
        Assert.Equal([("fire", 2, true)], Counts(after));

        // A followers-only log: only followers (and the author) can react.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/v1/reactions/log/{followersLog}/wow", cat)).StatusCode);
        await SendAsync(HttpMethod.Put, "/v1/users/ben/follow", cat);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/reactions/log/{followersLog}/wow", cat), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/reactions/log/{followersLog}/clap", ben), HttpStatusCode.OK);

        Assert.Equal("reaction", (await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/reactions/log/{publicLog}/poop", ana), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/v1/reactions/grab_bag/{publicLog}/fire", ana)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/v1/reactions/photo/{Guid.NewGuid()}/fire", ana)).StatusCode);

        // A deleted account's reactions stop counting.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET deleted_at = now() WHERE handle = 'cat'");
        }

        Assert.Equal([("fire", 1, true)], Counts(await FeedLogAsync(ana, publicLog)));
    }

    [Fact]
    public async Task Prompt_answers_take_reactions_too()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var prompt = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/prompts/today", ben), HttpStatusCode.OK)).GetProperty("prompt").GetProperty("id").GetString();
        var answer = (await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{prompt}/answer", ben, new { body = "The Velvet Ropes" }), HttpStatusCode.OK))
            .GetProperty("my_answer").GetProperty("id").GetString();

        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/reactions/prompt_answer/{answer}/laugh", ana), HttpStatusCode.OK);

        var feed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed?limit=100", ana), HttpStatusCode.OK);
        var item = feed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("type").GetString() == "prompt_answer");
        Assert.Equal([("laugh", 1, true)], Counts(item));
    }

    private static (string, int, bool) Single(JsonElement withReactions) => Assert.Single(Counts(withReactions));

    private static List<(string, int, bool)> Counts(JsonElement withReactions) =>
        [.. withReactions.GetProperty("reactions").EnumerateArray()
            .Select(r => (r.GetProperty("reaction").GetString()!, r.GetProperty("count").GetInt32(), r.GetProperty("mine").GetBoolean()))];

    private async Task<JsonElement> FeedLogAsync(string token, Guid logId)
    {
        var feed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed?limit=100", token), HttpStatusCode.OK);
        return feed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == logId.ToString());
    }

    private async Task<(Guid Public, Guid Followers)> LogsAsync(string handle)
    {
        var artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        await using var connection = await _db.OpenConnectionAsync();
        var ids = (await connection.QueryAsync<Guid>(
            """
            INSERT INTO album_logs (user_id, album_id, visibility)
            SELECT u.id, al.id, v.visibility::visibility FROM users u, albums al, (VALUES (1, 'public'), (2, 'followers')) v (n, visibility)
             WHERE u.handle = @handle AND al.artist_id = @artistId
            RETURNING id
            """,
            new { handle, artistId })).ToList();
        var visibility = (await connection.QueryAsync<(Guid Id, string Visibility)>(
            "SELECT id, visibility::text FROM album_logs WHERE id = ANY(@ids)", new { ids = ids.ToArray() })).ToDictionary(r => r.Visibility, r => r.Id);
        return (visibility["public"], visibility["followers"]);
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
