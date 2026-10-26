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

/// <summary>Staff links to concert sources and the Ticketmaster fields on shows (migration 0030).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcertApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"concert-api-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"concert_api_{Guid.NewGuid():N}");
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
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, recursive: true);
        }
    }

    [Fact]
    public async Task Staff_link_an_artist_to_Ticketmaster_and_a_sync_is_queued()
    {
        var fan = await SignInAsync("fan");
        var staff = await StaffAsync("staffer");
        var other = await ScoringSetup.ActiveArtistAsync(_db, "sp-other", "Other", releases: 1);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "DELETE FROM jobs; INSERT INTO artist_sources (artist_id, source, external_id, matched_by) VALUES (@other, 'ticketmaster', 'K8vZOther1', 'spotify_link')",
                new { other });
        }

        var path = $"/v1/admin/artists/{_artistId}/sources";
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, path, fan)).StatusCode);
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, path, staff), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());

        var invalid = await JsonAsync(await SendAsync(HttpMethod.Put, $"{path}/ticketmaster", staff,
            new { external_id = "https://www.ticketmaster.com/geese-tickets/artist/2832334" }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("validation_failed", invalid.GetProperty("code").GetString());
        await JsonAsync(await SendAsync(HttpMethod.Put, $"{path}/ticketmaster", staff, new { external_id = "K8vZOther1" }), HttpStatusCode.Conflict);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"{path}/seatgeek", staff, new { external_id = "K8vZGeese1" }), HttpStatusCode.NotFound);

        var linked = await JsonAsync(await SendAsync(HttpMethod.Put, $"{path}/ticketmaster", staff, new { external_id = "K8vZGeese1" }), HttpStatusCode.OK);
        Assert.Equal(("ticketmaster", "K8vZGeese1", "staff"), (linked.GetProperty("source").GetString(),
            linked.GetProperty("external_id").GetString(), linked.GetProperty("matched_by").GetString()));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*) FROM jobs WHERE kind = 'concerts.sync_artist' AND payload->>'artist_id' = @id", new { id = _artistId.ToString() }));
        var list = await JsonAsync(await SendAsync(HttpMethod.Get, path, staff), HttpStatusCode.OK);
        Assert.Equal("K8vZGeese1", Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("external_id").GetString());

        await JsonAsync(await SendAsync(HttpMethod.Delete, $"{path}/ticketmaster", staff), HttpStatusCode.NoContent);
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, path, staff), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/artists/{Guid.NewGuid()}/sources", staff), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Shows_from_Ticketmaster_carry_the_ticket_link_status_sales_and_upcoming_presales()
    {
        var fan = await SignInAsync("fan");
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO events (artist_id, name, venue, city_id, city, country, starts_at, external_source, external_id, ticket_url,
                                    status, public_sale_starts_at, time_tba)
                VALUES (@_artistId, 'Geese', 'Metro', 'us-il-chicago', 'Chicago', 'US', now() + interval '30 days', 'ticketmaster', 'ev-1',
                        'https://www.ticketmaster.com/event/ev-1', 'onsale', now() + interval '3 days', false),
                       (@_artistId, 'Geese (staff)', 'Riviera', 'us-il-chicago', 'Chicago', 'US', now() + interval '40 days', NULL, NULL, NULL,
                        'scheduled', NULL, false);
                INSERT INTO event_presales (event_id, name, starts_at, ends_at)
                SELECT id, 'Verified Fan Presale', now() + interval '1 day', now() + interval '2 days' FROM events WHERE external_id = 'ev-1'
                UNION ALL
                SELECT id, 'Old Presale', now() - interval '3 days', now() - interval '1 day' FROM events WHERE external_id = 'ev-1';
                """,
                new { _artistId });
        }

        var page = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{_artistId}/events", fan), HttpStatusCode.OK);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        var synced = items[0];
        Assert.Equal(("ticketmaster", "https://www.ticketmaster.com/event/ev-1", "onsale", false), (synced.GetProperty("source").GetString(),
            synced.GetProperty("ticket_url").GetString(), synced.GetProperty("status").GetString(), synced.GetProperty("time_tba").GetBoolean()));
        Assert.Equal(JsonValueKind.String, synced.GetProperty("public_sale_starts_at").ValueKind);
        var presale = Assert.Single(synced.GetProperty("presales").EnumerateArray());
        Assert.Equal("Verified Fan Presale", presale.GetProperty("name").GetString());

        var staffShow = items[1];
        Assert.Equal((JsonValueKind.Null, "scheduled", 0), (staffShow.GetProperty("source").ValueKind, staffShow.GetProperty("status").GetString(),
            staffShow.GetProperty("presales").GetArrayLength()));

        var detail = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/events/{synced.GetProperty("id").GetString()}", fan), HttpStatusCode.OK);
        Assert.Single(detail.GetProperty("event").GetProperty("presales").EnumerateArray());
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
