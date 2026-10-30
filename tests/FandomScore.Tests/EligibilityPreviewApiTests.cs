using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Tests.Contract;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// The eligible-fan preview promoters see before opening registration
/// (<c>GET /organizations/{id}/allocations/{id}/eligibility-preview</c>): counts only, the pool registration would let
/// in, local fans apart from others, and the counts at other thresholds.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EligibilityPreviewApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"eligibility_preview_{Guid.NewGuid():N}");
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
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-preview", "Preview Band", releases: 3);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Counts_the_pool_registration_would_let_in_split_by_hub_city_at_the_threshold_and_alternatives()
    {
        var (id, owner) = await OrganizationAsync("Preview Presents", "preview_owner");
        var analyst = await SignInAsync("preview_analyst");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/preview_analyst", owner, new { role = "analyst" }), HttpStatusCode.OK);
        var allocationId = await AllocationAsync(id, owner, tickets: 2, minRawScore: 10);

        await FanAsync("pv_local_high", 80m, "us-il-chicago");            // eligible, local
        await FanAsync("pv_local_mid", 15m, "us-il-chicago");             // eligible, local; drops out at 20
        await FanAsync("pv_local_low", 5m, "us-il-chicago");              // below 10, above 5
        await FanAsync("pv_away_high", 50m, "us-ny-new-york");            // eligible, not local
        await FanAsync("pv_held", 90m, "us-il-chicago", onHold: true);    // excluded: fraud hold
        await FanAsync("pv_no_hub", 70m, hubCity: null);                  // excluded: no hub city, as registration refuses
        await FanAsync("pv_gone", 95m, "us-il-chicago", deleted: true);   // excluded: deleted account

        var preview = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}/allocations/{allocationId}/eligibility-preview", analyst), HttpStatusCode.OK);
        Assert.Equal(allocationId, preview.GetProperty("allocation_id").GetString());
        Assert.Equal((2, 10m, false), (preview.GetProperty("tickets").GetInt32(), preview.GetProperty("threshold").GetDecimal(), preview.GetProperty("threshold_is_default").GetBoolean()));
        Assert.Equal((4, 3), (preview.GetProperty("scored_fans").GetInt32(), preview.GetProperty("excluded").GetInt32()));
        Assert.Equal((10m, 3, 2, 1), Count(preview.GetProperty("eligible")));
        Assert.Equal(
        [
            (5m, 4, 3, 1),
            (10m, 3, 2, 1),
            (20m, 2, 1, 1),
            (40m, 2, 1, 1),
        ], preview.GetProperty("alternatives").EnumerateArray().Select(Count));

        // Aggregates only: nothing that names, scores, or places a fan.
        var text = preview.GetRawText();
        foreach (var hidden in new[] { "handle", "email", "raw_score", "hub_city", "user_id", "items" })
        {
            Assert.DoesNotContain(hidden, text, StringComparison.Ordinal);
        }

        // Outsiders get the organization's usual not-found; a signed-out call is unauthorized.
        var stranger = await SignInAsync("preview_stranger");
        var denied = await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}/allocations/{allocationId}/eligibility-preview", stranger);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal("not_found", (await JsonAsync(denied, HttpStatusCode.NotFound)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"/v1/organizations/{id}/allocations/{allocationId}/eligibility-preview")).StatusCode);
    }

    [Fact]
    public async Task A_draft_without_a_threshold_previews_the_one_publishing_would_set_and_alternatives_around_it()
    {
        var (id, owner) = await OrganizationAsync("Draft Presents", "draft_owner");
        var allocationId = await AllocationAsync(id, owner, tickets: 5, minRawScore: null);
        await FanAsync("dr_fan", 1000m, "us-il-chicago");

        await using var connection = await _db.OpenConnectionAsync();
        var expected = await connection.ExecuteScalarAsync<decimal>("SELECT allocation_default_threshold(@_artistId)", new { _artistId });
        Assert.True(expected > 0, "the test artist needs a positive default threshold for the alternatives to differ");

        var preview = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}/allocations/{allocationId}/eligibility-preview", owner), HttpStatusCode.OK);
        Assert.Equal((expected, true), (preview.GetProperty("threshold").GetDecimal(), preview.GetProperty("threshold_is_default").GetBoolean()));
        Assert.Equal((expected, 1, 1, 0), Count(preview.GetProperty("eligible")));
        Assert.Equal([Math.Round(expected / 2, 2), expected, expected * 2, expected * 4],
            preview.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("threshold").GetDecimal()));

        // Publishing sets min_raw_score to that value; the preview then reports it as set.
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{id}/allocations/{allocationId}/publish", owner), HttpStatusCode.OK);
        var published = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}/allocations/{allocationId}/eligibility-preview", owner), HttpStatusCode.OK);
        Assert.Equal((expected, false), (published.GetProperty("threshold").GetDecimal(), published.GetProperty("threshold_is_default").GetBoolean()));
    }

    // ------------------------------------------------------------------ helpers

    private static (decimal, int, int, int) Count(JsonElement count) => (
        count.GetProperty("threshold").GetDecimal(), count.GetProperty("total").GetInt32(),
        count.GetProperty("local").GetInt32(), count.GetProperty("other").GetInt32());

    private async Task<string> AllocationAsync(string organizationId, string token, int tickets, decimal? minRawScore)
    {
        var show = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{organizationId}/events", token, new
        {
            artist_id = _artistId, name = "Preview show", venue = "Hall", city_id = "us-il-chicago",
            starts_at = DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
        }), HttpStatusCode.Created);
        var eventId = show.GetProperty("event").GetProperty("id").GetString();
        var body = new Dictionary<string, object?>
        {
            ["tickets"] = tickets, ["face_value_cents"] = 4500,
            ["registration_opens_at"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
            ["registration_closes_at"] = DateTimeOffset.UtcNow.AddDays(3).ToString("O"),
        };
        if (minRawScore is not null)
        {
            body["min_raw_score"] = minRawScore;
        }

        var allocation = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{organizationId}/events/{eventId}/allocations", token, body), HttpStatusCode.Created);
        return allocation.GetProperty("id").GetString()!;
    }

    private async Task<(string Id, string OwnerToken)> OrganizationAsync(string name, string ownerHandle)
    {
        var staff = await SignInAsync($"staff_{ownerHandle}");
        var owner = await SignInAsync(ownerHandle);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = @handle", new { handle = $"staff_{ownerHandle}" });
        }

        var created = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/organizations", staff,
            new { name, owner_handle = ownerHandle }), HttpStatusCode.Created);
        return (created.GetProperty("id").GetString()!, owner);
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    /// <summary>A fan with a score for the artist, a hub city (or none), and optionally on fraud hold or deleted.</summary>
    private async Task FanAsync(string handle, decimal score, string? hubCity, bool onHold = false, bool deleted = false)
    {
        var token = await SignInAsync(handle);
        if (hubCity is not null)
        {
            await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token, new { city_ids = new[] { hubCity } }), HttpStatusCode.OK);
        }

        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
            SELECT id, @_artistId, @score, 50, false, 0.5 FROM users WHERE handle = @handle
            """,
            new { _artistId, score, handle });
        if (onHold)
        {
            await connection.ExecuteAsync(
                "INSERT INTO fraud_flags (user_id, reason, source) SELECT id, 'streaming_farm', 'rule' FROM users WHERE handle = @handle",
                new { handle });
        }

        if (deleted)
        {
            await connection.ExecuteAsync("UPDATE users SET deleted_at = now() WHERE handle = @handle", new { handle });
        }
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
