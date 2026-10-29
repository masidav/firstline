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

/// <summary>Artist and merch links set by staff, and anonymous outbound click counts (migration 0038).</summary>
[Collection(PostgresCollection.Name)]
public sealed class LinkApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"link-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"links_{Guid.NewGuid():N}");
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
    public async Task Staff_set_https_links_and_the_artist_shows_them()
    {
        var fan = await SignInAsync("fan");
        var staff = await StaffAsync("staffer");
        var path = $"/v1/admin/artists/{_artistId}/links";
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Patch, path, fan, new { website_url = "https://geese.example.com" })).StatusCode);

        var bad = await JsonAsync(await SendAsync(HttpMethod.Patch, path, staff, new { website_url = "http://geese.example.com", merch_url = "javascript:alert(1)" }),
            HttpStatusCode.UnprocessableEntity);
        Assert.Equal(2, bad.GetProperty("errors").GetArrayLength());

        var set = await JsonAsync(await SendAsync(HttpMethod.Patch, path, staff,
            new { website_url = "https://geese.example.com", merch_url = " https://shop.geese.example.com " }), HttpStatusCode.OK);
        Assert.Equal(("https://geese.example.com", "https://shop.geese.example.com"), (set.GetProperty("website_url").GetString(), set.GetProperty("merch_url").GetString()));
        var artist = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{_artistId}", fan), HttpStatusCode.OK);
        Assert.Equal("https://shop.geese.example.com", artist.GetProperty("merch_url").GetString());

        // A missing field stays; null clears.
        var cleared = await JsonAsync(await SendAsync(HttpMethod.Patch, path, staff, new { merch_url = (string?)null }), HttpStatusCode.OK);
        Assert.Equal(("https://geese.example.com", JsonValueKind.Null), (cleared.GetProperty("website_url").GetString(), cleared.GetProperty("merch_url").ValueKind));
        await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/artists/{Guid.NewGuid()}/links", staff, new { website_url = "https://x.example.com" }), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Clicks_are_counted_without_who_clicked_and_staff_see_them_per_artist()
    {
        var fan = await SignInAsync("fan");
        var staff = await StaffAsync("staffer");
        Guid eventId;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            eventId = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO events (artist_id, name, starts_at, ticket_url) VALUES (@_artistId, 'Geese at Metro', now() + interval '20 days', 'https://tm.example.com/e') RETURNING id",
                new { _artistId });
        }

        foreach (var kind in new[] { "artist_merch", "artist_merch", "artist_spotify" })
        {
            await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/clicks", fan, new { kind, artist_id = _artistId }), HttpStatusCode.NoContent);
        }

        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/clicks", fan, new { kind = "event_tickets", artist_id = _artistId, event_id = eventId }), HttpStatusCode.NoContent);

        // Tickets need the show; other links must not have one; the show must be the artist's.
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/clicks", fan, new { kind = "event_tickets", artist_id = _artistId }), HttpStatusCode.UnprocessableEntity);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/clicks", fan, new { kind = "artist_merch", artist_id = _artistId, event_id = eventId }), HttpStatusCode.UnprocessableEntity);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/clicks", fan, new { kind = "event_tickets", artist_id = _artistId, event_id = Guid.NewGuid() }), HttpStatusCode.NotFound);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/clicks", fan, new { kind = "purchase", artist_id = _artistId }), HttpStatusCode.UnprocessableEntity);

        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'link_clicks' AND column_name LIKE '%user%'"));

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/v1/admin/clicks", fan)).StatusCode);
        var summary = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/clicks?days=7", staff), HttpStatusCode.OK);
        Assert.Equal(7, summary.GetProperty("days").GetInt32());
        Assert.Equal([("artist_merch", 2), ("artist_spotify", 1), ("event_tickets", 1)],
            summary.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("kind").GetString(), i.GetProperty("clicks").GetInt32())));
        await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/clicks?days=0", staff), HttpStatusCode.UnprocessableEntity);
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
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
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
