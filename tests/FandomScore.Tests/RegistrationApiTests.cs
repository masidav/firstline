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

/// <summary>Fan registration for allocations: eligibility, register, withdraw, and the snapshot (migration 0011).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RegistrationApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private Guid _eventId;
    private Guid _allocationId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"registrations_{Guid.NewGuid():N}");
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

        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        (_eventId, _allocationId) = await ShowWithAllocationAsync(minPercentile: 50);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task An_eligible_fan_registers_with_a_snapshot_of_hubs_and_score()
    {
        var fan = await FanAsync("local_fan", percentile: 91.2m, "us-ny-new-york", "us-il-chicago");

        var eligibility = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{_allocationId}/eligibility", fan), HttpStatusCode.OK);
        Assert.True(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, eligibility.GetProperty("reason").ValueKind);
        Assert.Equal((91.2m, 50m, 2), (eligibility.GetProperty("percentile").GetDecimal(),
            eligibility.GetProperty("min_percentile").GetDecimal(), eligibility.GetProperty("hub_match").GetInt32()));

        var response = await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan);
        var registration = await JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal($"/v1/allocations/{_allocationId}/registration", response.Headers.Location?.ToString());
        Assert.Equal(["us-ny-new-york", "us-il-chicago"], registration.GetProperty("hub_city_ids").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal((2, true, 91.2m, "registered"), (registration.GetProperty("hub_match").GetInt32(),
            registration.GetProperty("local").GetBoolean(), registration.GetProperty("percentile").GetDecimal(),
            registration.GetProperty("status").GetString()));

        // Registering again returns the same registration.
        var again = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.OK);
        Assert.Equal(registration.GetProperty("id").GetString(), again.GetProperty("id").GetString());

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", fan), HttpStatusCode.OK);
        var item = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal(_allocationId.ToString(), item.GetProperty("allocation").GetProperty("id").GetString());
        Assert.Equal("Chicago", item.GetProperty("event").GetProperty("city").GetString());
    }

    [Fact]
    public async Task A_fan_from_elsewhere_can_register_but_is_not_local()
    {
        var fan = await FanAsync("far_fan", percentile: 60, "ca-on-toronto");

        var registration = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan),
            HttpStatusCode.Created);

        Assert.False(registration.GetProperty("local").GetBoolean());
        Assert.Equal(JsonValueKind.Null, registration.GetProperty("hub_match").ValueKind);
    }

    [Theory]
    [InlineData(40, "below_threshold", HttpStatusCode.Forbidden, "not_eligible")]
    [InlineData(null, "no_score", HttpStatusCode.Forbidden, "not_eligible")]
    public async Task Fans_below_the_threshold_or_without_a_score_are_told_why(int? percentile, string reason, HttpStatusCode status, string code)
    {
        var fan = await FanAsync("fan_x", percentile, "us-il-chicago");

        var eligibility = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{_allocationId}/eligibility", fan), HttpStatusCode.OK);
        var problem = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), status);

        Assert.False(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal(reason, eligibility.GetProperty("reason").GetString());
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_fan_without_a_hub_city_must_choose_one_first()
    {
        var fan = await FanAsync("no_hub", percentile: 99);

        var problem = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.Conflict);

        Assert.Equal("hub_city_required", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task One_registration_per_show_until_withdrawn()
    {
        var fan = await FanAsync("one_show", percentile: 99, "us-il-chicago");
        var second = await AllocationAsync(_eventId, minPercentile: 0);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.Created);

        var problem = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{second}/registration", fan), HttpStatusCode.Conflict);
        Assert.Equal("already_registered", problem.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/allocations/{_allocationId}/registration", fan)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/v1/allocations/{_allocationId}/registration", fan)).StatusCode);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{second}/registration", fan), HttpStatusCode.Created);

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", fan), HttpStatusCode.OK);
        Assert.Equal(["registered", "withdrawn"], mine.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("registration").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Nobody_registers_before_opening_or_for_drafts()
    {
        var fan = await FanAsync("early", percentile: 99, "us-il-chicago");
        var notYet = await AllocationAsync(_eventId, minPercentile: 0, opensInHours: 24);
        var draft = await AllocationAsync(_eventId, minPercentile: 0, publish: false);

        var closed = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{notYet}/registration", fan), HttpStatusCode.Conflict);
        Assert.Equal("registration_closed", closed.GetProperty("code").GetString());
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{draft}/registration", fan), HttpStatusCode.NotFound);
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{draft}/eligibility", fan), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Withdrawing_after_registration_closes_is_refused()
    {
        var fan = await FanAsync("late", percentile: 99, "us-il-chicago");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.Created);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            // Close the window directly (the API can't change an open allocation's rules, by design).
            await connection.ExecuteAsync(
                """
                ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
                UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour'
                 WHERE id = @_allocationId;
                ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
                """,
                new { _allocationId });
        }

        var problem = await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/allocations/{_allocationId}/registration", fan), HttpStatusCode.Conflict);

        Assert.Equal("registration_closed", problem.GetProperty("code").GetString());
    }

    private async Task<(Guid EventId, Guid AllocationId)> ShowWithAllocationAsync(decimal minPercentile)
    {
        await using var connection = await _db.OpenConnectionAsync();
        var eventId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, city_id, starts_at) VALUES (@_artistId, 'Geese', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
        return (eventId, await AllocationAsync(eventId, minPercentile));
    }

    private async Task<Guid> AllocationAsync(Guid eventId, decimal minPercentile, double opensInHours = -1, bool publish = true)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_percentile, registration_opens_at, registration_closes_at, published_at)
            VALUES (@eventId, 10, 4500, @minPercentile, now() + make_interval(secs => @opens * 3600), now() + interval '5 days',
                    CASE WHEN @publish THEN now() END)
            RETURNING id
            """,
            new { eventId, minPercentile, opens = opensInHours, publish });
    }

    /// <summary>A signed-in fan with the given hub cities and, unless null, a score for the artist.</summary>
    private async Task<string> FanAsync(string handle, decimal? percentile, params string[] hubs)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        var token = (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
        if (hubs.Length > 0)
        {
            await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token, new { city_ids = hubs }), HttpStatusCode.OK);
        }

        if (percentile is not null)
        {
            await using var connection = await _db.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                SELECT id, @_artistId, 50, @percentile, true, 0.9 FROM users WHERE handle = @handle
                """,
                new { _artistId, percentile, handle });
        }

        return token;
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
