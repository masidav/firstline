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
/// The pilot case-study report (Phase 11): the numbers for a show follow its registrations, draw, returns, and
/// re-offers through to show start; scalping complaints are entered by hand (migration 0047).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ShowReportApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] Chicago = ["us-il-chicago"];
    private static readonly string[] Phoenix = ["us-az-phoenix"];

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private string _organizationId = "";
    private string _owner = "";
    private Guid _eventId;
    private Guid _allocationId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"show_report_{Guid.NewGuid():N}");
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
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);

        var staff = await StaffAsync("report_staff");
        _owner = await SignInAsync("metro_owner");
        _organizationId = (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/organizations", staff,
            new { name = "Metro Shows", owner_handle = "metro_owner" }), HttpStatusCode.Created)).GetProperty("id").GetString()!;
        var show = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{_organizationId}/events", _owner, new
        {
            artist_id = _artistId, name = "Geese at Metro", venue = "Metro", city_id = "us-il-chicago",
            starts_at = DateTimeOffset.UtcNow.AddDays(20).ToString("O"),
        }), HttpStatusCode.Created);
        _eventId = show.GetProperty("event").GetProperty("id").GetGuid();
        await using var connection = await _db.OpenConnectionAsync();
        // Three seats, registration closing in moments so the draw can run; a draft beside it that the report leaves out.
        _allocationId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at,
                                     published_at, delivery_hours_before_show)
            VALUES (@_eventId, 3, 4500, 10, now() - interval '1 hour', now() + interval '2 seconds', now(), 36) RETURNING id
            """,
            new { _eventId });
        await connection.ExecuteAsync(
            """
            INSERT INTO allocations (event_id, name, tickets, face_value_cents, registration_opens_at, registration_closes_at)
            VALUES (@_eventId, 'Draft', 50, 4500, now() + interval '1 day', now() + interval '2 days')
            """,
            new { _eventId });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task The_report_follows_the_show_from_registration_through_the_draw_returns_re_offers_and_empty_seats()
    {
        var ana = await FanAsync("ana", 90, Chicago, plusOne: true);   // hub, 2 seats
        var ben = await FanAsync("ben", 80, Phoenix);                  // not a hub fan: ranked after every hub fan
        var cat = await FanAsync("cat", 70, Chicago);                  // hub, the third seat
        await FanAsync("dee", 60, Chicago);                            // next in line, then Ben
        var eve = await SignInAsync("eve");                            // below the threshold: refused and logged
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", eve, new { city_ids = Chicago }), HttpStatusCode.OK);
        await ScoreAsync("eve", 1);
        var refused = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", eve, new { }), HttpStatusCode.Forbidden);
        Assert.Equal("not_eligible", refused.GetProperty("code").GetString());

        var before = await ReportAsync();
        Assert.Equal((false, 1), (before.GetProperty("show_started").GetBoolean(), before.GetProperty("allocations").GetArrayLength()));
        var open = before.GetProperty("totals");
        Assert.Equal((3, 4, 4, 1, 3, 1, 0, 0), (Int(open, "seats_offered"), Int(open, "registrations"), Int(open, "eligible_registrations"),
            Int(open, "ineligible_attempts"), Int(open, "hub_registrations"), Int(open, "non_hub_registrations"), Int(open, "selected_fans"),
            Int(open, "seats_filled")));
        Assert.Equal(JsonValueKind.Null, open.GetProperty("empty_seats").ValueKind);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("scalping_complaints").ValueKind);
        Assert.Equal(0m, open.GetProperty("sell_through_percent").GetDecimal());

        await RunDrawAsync();
        var drawn = (await ReportAsync()).GetProperty("allocations")[0];
        Assert.Equal(_allocationId, drawn.GetProperty("allocation").GetProperty("id").GetGuid());
        var m = drawn.GetProperty("metrics");
        Assert.Equal((2, 2, 1, 3, 3, 0, 0, 0, 100.0m), (Int(m, "selected_fans"), Int(m, "selected_in_draw"), Int(m, "plus_ones"), Int(m, "seats_filled"),
            Int(m, "hub_seats_filled"), Int(m, "non_hub_seats_filled"), Int(m, "tickets_delivered"), Int(m, "seats_unfilled"),
            m.GetProperty("sell_through_percent").GetDecimal()));

        // Tickets go out: delivered seats count. Then Ana returns her two seats (Dee and Ben get one each, a re-offer),
        // and Cat returns hers with nobody left in the ranking, so one seat stays free.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("ALTER TABLE reservations DISABLE TRIGGER trg_reservation_guard; UPDATE reservations SET deliver_at = now() - interval '1 minute'; ALTER TABLE reservations ENABLE TRIGGER trg_reservation_guard;");
        }

        Assert.Equal(3, Int((await ReportAsync()).GetProperty("totals"), "tickets_delivered"));
        foreach (var fan in new[] { ana, cat })
        {
            var id = Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", fan), HttpStatusCode.OK)).GetProperty("items").EnumerateArray())
                .GetProperty("id").GetString();
            await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/reservations/{id}/return", fan), HttpStatusCode.OK);
        }

        var after = (await ReportAsync()).GetProperty("totals");
        Assert.Equal((4, 2, 0, 2, 3, 2, 2, 2, 1, 1, 1, 0, 66.7m), (Int(after, "selected_fans"), Int(after, "selected_in_draw"), Int(after, "plus_ones"),
            Int(after, "returns"), Int(after, "seats_returned"), Int(after, "re_offers"), Int(after, "seats_re_offered"), Int(after, "seats_filled"),
            Int(after, "hub_seats_filled"), Int(after, "non_hub_seats_filled"), Int(after, "seats_unfilled"), Int(after, "tickets_delivered"),
            after.GetProperty("sell_through_percent").GetDecimal()));
        Assert.Equal("held", Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", ben), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("empty_seats").ValueKind);

        // Show time: the free seat is an empty seat.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("ALTER TABLE events DISABLE TRIGGER USER; UPDATE events SET starts_at = now() - interval '1 minute' WHERE id = @_eventId; ALTER TABLE events ENABLE TRIGGER USER;", new { _eventId });
        }

        var started = await ReportAsync();
        Assert.True(started.GetProperty("show_started").GetBoolean());
        Assert.Equal(1, Int(started.GetProperty("totals"), "empty_seats"));
        Assert.Equal(1, Int(started.GetProperty("allocations")[0].GetProperty("metrics"), "empty_seats"));
    }

    [Fact]
    public async Task Scalping_complaints_are_entered_by_hand_by_admins_or_staff_and_the_report_is_for_members_and_staff_only()
    {
        var analyst = await SignInAsync("metro_analyst");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{_organizationId}/members/metro_analyst", _owner, new { role = "analyst" }), HttpStatusCode.OK);
        var complaintsPath = $"/v1/organizations/{_organizationId}/allocations/{_allocationId}/complaints";

        var saved = await JsonAsync(await SendAsync(HttpMethod.Put, complaintsPath, _owner, new { scalping_complaints = 2, note = "Two listings on a resale site, both fakes." }), HttpStatusCode.OK);
        Assert.Equal((2, "metro_owner"), (Int(saved, "scalping_complaints"), saved.GetProperty("updated_by_handle").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, complaintsPath, analyst, new { scalping_complaints = 1 })).StatusCode);
        var invalid = await JsonAsync(await SendAsync(HttpMethod.Put, complaintsPath, _owner, new { scalping_complaints = -1 }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("scalping_complaints", invalid.GetProperty("errors")[0].GetProperty("field").GetString());

        var report = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{_organizationId}/events/{_eventId}/report", analyst), HttpStatusCode.OK);
        var allocation = Assert.Single(report.GetProperty("allocations").EnumerateArray());
        Assert.Equal("Two listings on a resale site, both fakes.", allocation.GetProperty("complaints").GetProperty("note").GetString());
        Assert.Equal(2, Int(allocation.GetProperty("metrics"), "scalping_complaints"));
        Assert.Equal(2, Int(report.GetProperty("totals"), "scalping_complaints"));

        // Staff replace the entry; the latest wins, and the note can be dropped.
        var staff = await StaffAsync("report_staff2");
        var replaced = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/admin/allocations/{_allocationId}/complaints", staff, new { scalping_complaints = 0, note = (string?)null }), HttpStatusCode.OK);
        Assert.Equal((0, JsonValueKind.Null), (Int(replaced, "scalping_complaints"), replaced.GetProperty("note").ValueKind));
        var staffReport = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/events/{_eventId}/report", staff), HttpStatusCode.OK);
        Assert.Equal(0, Int(staffReport.GetProperty("totals"), "scalping_complaints"));

        // Fans and other organizations don't see it.
        var stranger = await SignInAsync("stranger");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, $"/v1/admin/events/{_eventId}/report", stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/organizations/{_organizationId}/events/{_eventId}/report", stranger)).StatusCode);
        var (otherOrganization, rival) = await OrganizationAsync("South Stage", "south_owner");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/organizations/{otherOrganization}/events/{_eventId}/report", rival)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/v1/organizations/{otherOrganization}/allocations/{_allocationId}/complaints", rival, new { scalping_complaints = 9 })).StatusCode);
    }

    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private async Task<JsonElement> ReportAsync() =>
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{_organizationId}/events/{_eventId}/report", _owner), HttpStatusCode.OK);

    private async Task RunDrawAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));   // registration closes
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{_organizationId}/allocations/{_allocationId}/selection", _owner), HttpStatusCode.Accepted);
        await Draws.RunQueuedAsync(_db);
    }

    private async Task<(string Id, string Owner)> OrganizationAsync(string name, string ownerHandle)
    {
        var staff = await StaffAsync($"staff_for_{ownerHandle}");
        var owner = await SignInAsync(ownerHandle);
        var created = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/organizations", staff, new { name, owner_handle = ownerHandle }), HttpStatusCode.Created);
        return (created.GetProperty("id").GetString()!, owner);
    }

    private async Task<string> FanAsync(string handle, decimal score, string[] hubs, bool plusOne = false)
    {
        var token = await SignInAsync(handle);
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token, new { city_ids = hubs }), HttpStatusCode.OK);
        await ScoreAsync(handle, score);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", token, new { plus_one = plusOne }), HttpStatusCode.Created);
        return token;
    }

    private async Task ScoreAsync(string handle, decimal score)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
            SELECT id, @_artistId, @score, least(@score, 100), false, 0.1 FROM users WHERE handle = @handle
            """,
            new { _artistId, score, handle });
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
