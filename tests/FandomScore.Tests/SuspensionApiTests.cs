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

/// <summary>Staff pausing accounts (bots), what a pause blocks, and asking to be unbanned (migration 0018).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SuspensionApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"suspension-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"suspension_api_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
    }

    [Fact]
    public async Task A_paused_fan_cant_post_or_register_and_sees_why_until_staff_lift_it()
    {
        var fan = await SignInAsync("botlike");
        var staff = await StaffAsync("staffer");
        var albumId = await AlbumAsync();
        var allocationId = await OpenAllocationAsync();

        var paused = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/botlike/suspension", staff,
            new { reason = "bot", days = 7, message = "Your account looks automated.", note = "Thousands of plays a day" }), HttpStatusCode.Created);
        Assert.Equal(("bot", "botlike"), (paused.GetProperty("reason").GetString(), paused.GetProperty("user").GetProperty("handle").GetString()));

        var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", fan), HttpStatusCode.OK);
        var suspension = me.GetProperty("suspension");
        Assert.Equal("Your account looks automated.", suspension.GetProperty("message").GetString());
        Assert.InRange(suspension.GetProperty("ends_at").GetDateTime(), DateTime.UtcNow.AddDays(6.9), DateTime.UtcNow.AddDays(7.1));
        Assert.False(suspension.TryGetProperty("note", out _));   // staff-only note never reaches the fan

        // Every way of posting or registering is refused with account_suspended.
        foreach (var blocked in new[]
                 {
                     await SendAsync(HttpMethod.Post, "/v1/uploads", fan, new { purpose = "submission", mime_type = "image/jpeg", bytes = 100 }),
                     await SendAsync(HttpMethod.Post, "/v1/logs", fan, new { album_id = albumId, rating = 8 }),
                     await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", fan, new { }),
                 })
        {
            var problem = await JsonAsync(blocked, HttpStatusCode.Forbidden);
            Assert.Equal("account_suspended", problem.GetProperty("code").GetString());
        }

        var eligibility = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{allocationId}/eligibility", fan), HttpStatusCode.OK);
        Assert.Equal("suspended", eligibility.GetProperty("reason").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/v1/admin/users/botlike/suspension/lift", staff,
            new { note = "Real fan after all" })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", fan), HttpStatusCode.OK))
            .GetProperty("suspension").ValueKind);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/logs", fan, new { album_id = albumId, rating = 8 }), HttpStatusCode.Created);

        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal(["suspend", "lift_suspension"], await connection.QueryAsync<string>(
            "SELECT action FROM moderation_actions WHERE target_type = 'account' ORDER BY created_at"));
    }

    [Fact]
    public async Task A_paused_fan_can_ask_once_to_be_unbanned_and_staff_lift_or_deny()
    {
        var fan = await SignInAsync("botlike");
        var staff = await StaffAsync("staffer");
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/botlike/suspension", staff, new { reason = "bot" }), HttpStatusCode.Created);

        var asked = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/suspension/appeal", fan,
            new { message = "I'm a real fan, I just listen a lot." }), HttpStatusCode.Created);
        var appeal = asked.GetProperty("appeal");
        Assert.Equal("pending", appeal.GetProperty("status").GetString());
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/suspension/appeal", fan, new { message = "Please" }), HttpStatusCode.Conflict);

        var queue = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/suspensions", staff), HttpStatusCode.OK);
        var item = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal("I'm a real fan, I just listen a lot.", item.GetProperty("appeal").GetProperty("message").GetString());

        var appealId = appeal.GetProperty("id").GetString();
        Assert.Equal("reply", (await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/appeals/{appealId}/decision", staff,
            new { decision = "deny" }), HttpStatusCode.UnprocessableEntity)).GetProperty("errors")[0].GetProperty("field").GetString());
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/appeals/{appealId}/decision", staff,
            new { decision = "deny", reply = "Your plays come from one device every second." }), HttpStatusCode.OK);

        var denied = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", fan), HttpStatusCode.OK)).GetProperty("suspension");
        Assert.Equal(("denied", "Your plays come from one device every second."),
            (denied.GetProperty("appeal").GetProperty("status").GetString(), denied.GetProperty("appeal").GetProperty("reply").GetString()));
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/appeals/{appealId}/decision", staff, new { decision = "lift" }),
            HttpStatusCode.Conflict);

        // A new pause gets a new request; lifting through the request ends the pause.
        await SendAsync(HttpMethod.Post, "/v1/admin/users/botlike/suspension/lift", staff);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/botlike/suspension", staff, new { reason = "other", days = 3 }), HttpStatusCode.Created);
        var second = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/suspension/appeal", fan, new { message = "Again, please" }), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/appeals/{second.GetProperty("appeal").GetProperty("id").GetString()}/decision",
            staff, new { decision = "lift", reply = "Sorry about that." }), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", fan), HttpStatusCode.OK))
            .GetProperty("suspension").ValueKind);
    }

    [Fact]
    public async Task Pausing_is_for_staff_and_checked()
    {
        var fan = await SignInAsync("fan_one");
        var staff = await StaffAsync("staffer");

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/v1/admin/users/staffer/suspension", fan, new { reason = "bot" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/v1/admin/suspensions", fan)).StatusCode);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/nobody_here/suspension", staff, new { reason = "bot" }), HttpStatusCode.NotFound);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/staffer/suspension", staff, new { reason = "bot" }), HttpStatusCode.UnprocessableEntity);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/fan_one/suspension", staff, new { reason = "spam" }), HttpStatusCode.UnprocessableEntity);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/fan_one/suspension", staff, new { reason = "bot", days = 400 }), HttpStatusCode.UnprocessableEntity);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/suspension/appeal", fan, new { message = "Hi" }), HttpStatusCode.NotFound);

        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/fan_one/suspension", staff, new { reason = "bot" }), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/fan_one/suspension", staff, new { reason = "bot" }), HttpStatusCode.Conflict);
        await SendAsync(HttpMethod.Post, "/v1/admin/users/fan_one/suspension/lift", staff);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/users/fan_one/suspension/lift", staff), HttpStatusCode.NotFound);
    }

    private async Task<Guid> AlbumAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>("SELECT id FROM albums WHERE artist_id = @_artistId LIMIT 1", new { _artistId });
    }

    private async Task<Guid> OpenAllocationAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        var eventId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, city_id, starts_at) VALUES (@_artistId, 'Geese', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, registration_opens_at, registration_closes_at, published_at)
            VALUES (@eventId, 10, 4500, now() - interval '1 hour', now() + interval '5 days', now()) RETURNING id
            """,
            new { eventId });
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
