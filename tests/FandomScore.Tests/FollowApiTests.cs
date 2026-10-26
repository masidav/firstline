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

/// <summary>Follows (migration 0018): follow, unfollow, remove a follower, lists, search, and what followers get to see.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FollowApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"follow_api_{Guid.NewGuid():N}");
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
    public async Task Following_needs_no_approval_is_idempotent_and_shows_in_both_lists()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, "/v1/users/ben/follow", ana)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, "/v1/users/@ben/follow", ana)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, "/v1/users/nobody/follow", ana)).StatusCode);
        var self = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ana), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("handle", self.GetProperty("errors")[0].GetProperty("field").GetString());

        var following = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/following", ana), HttpStatusCode.OK);
        var item = Assert.Single(following.GetProperty("items").EnumerateArray());
        Assert.Equal(("ben", true, false), (item.GetProperty("handle").GetString(), item.GetProperty("followed_by_me").GetBoolean(),
            item.GetProperty("follows_me").GetBoolean()));
        var followers = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/followers", ben), HttpStatusCode.OK);
        var follower = Assert.Single(followers.GetProperty("items").EnumerateArray());
        Assert.Equal(("ana", false, true), (follower.GetProperty("handle").GetString(), follower.GetProperty("followed_by_me").GetBoolean(),
            follower.GetProperty("follows_me").GetBoolean()));

        var profile = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ben", ana), HttpStatusCode.OK);
        Assert.Equal((true, false, 1, 0), (profile.GetProperty("followed_by_me").GetBoolean(), profile.GetProperty("follows_me").GetBoolean(),
            profile.GetProperty("follower_count").GetInt32(), profile.GetProperty("following_count").GetInt32()));

        // Ben removes Ana as a follower; the relationship is gone for both.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/v1/me/followers/ana", ben)).StatusCode);
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/following", ana), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());

        // Unfollowing when not following is fine.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/v1/users/ben/follow", ana)).StatusCode);
    }

    [Fact]
    public async Task Search_finds_fans_by_handle_or_name_and_leaves_the_caller_out()
    {
        var ana = await SignInAsync("ana");
        await SignInAsync("anabel");
        await SignInAsync("ben");
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET display_name = 'Ana Banana' WHERE handle = 'ben'");
        }

        var found = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users?q=@ana", ana), HttpStatusCode.OK);
        Assert.Equal(["anabel", "ben"], found.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("handle").GetString()));
        var byWord = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users?q=banana", ana), HttpStatusCode.OK);
        Assert.Equal("ben", Assert.Single(byWord.GetProperty("items").EnumerateArray()).GetProperty("handle").GetString());
        var empty = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users?q=", ana), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("q", empty.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Followers_see_followers_only_logs_and_can_report_them_and_a_deleted_account_drops_out()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var cat = await SignInAsync("cat");
        var artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        Guid logId;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            logId = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO album_logs (user_id, album_id, rating, visibility)
                SELECT u.id, al.id, 8, 'followers' FROM users u, albums al WHERE u.handle = 'ana' AND al.artist_id = @artistId RETURNING id
                """,
                new { artistId });
        }

        await SendAsync(HttpMethod.Put, "/v1/users/ana/follow", ben);

        Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/logs", ben), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana/logs", cat), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/v1/logs/{logId}", ben)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/logs/{logId}", cat)).StatusCode);
        Assert.Equal(1, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana", ben), HttpStatusCode.OK)).GetProperty("log_count").GetInt32());
        Assert.Equal(0, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana", cat), HttpStatusCode.OK)).GetProperty("log_count").GetInt32());

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/v1/reports", ben,
            new { target_type = "log", target_id = logId, reason = "spam" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/v1/reports", cat,
            new { target_type = "log", target_id = logId, reason = "spam" })).StatusCode);

        // Ana deletes her account: Ben's follow disappears and her handle is gone.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET deleted_at = now() WHERE handle = 'ana'");
        }

        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/following", ben), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/v1/users/ana", ben)).StatusCode);
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
