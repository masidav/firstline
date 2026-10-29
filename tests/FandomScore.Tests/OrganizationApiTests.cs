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
/// Promoter organizations (migration 0037): staff create them, owners manage members, admins run their own shows
/// from creation to draw, analysts read, and no organization reaches another's shows or staff-managed ones.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrganizationApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] Chicago = ["us-il-chicago"];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"organizations_{Guid.NewGuid():N}");
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
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-lakeside", "Lakeside", releases: 1);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Staff_create_an_organization_with_its_owner_and_only_members_see_it()
    {
        var staff = await StaffAsync("org_staff");
        var owner = await SignInAsync("lake_owner");
        var stranger = await SignInAsync("stranger");

        var created = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/organizations", staff,
            new { name = "Lakeside Presents", owner_handle = "@lake_owner" }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetString();
        Assert.Equal(("promoter", JsonValueKind.Null), (created.GetProperty("kind").GetString(), created.GetProperty("my_role").ValueKind));
        Assert.Equal([("lake_owner", "owner")], Members(created));

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/v1/admin/organizations", owner,
            new { name = "Self Serve", owner_handle = "lake_owner" })).StatusCode);
        var taken = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/organizations", staff,
            new { name = " lakeside presents ", owner_handle = "lake_owner" }), HttpStatusCode.Conflict);
        Assert.Equal("conflict", taken.GetProperty("code").GetString());
        var unknown = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/organizations", staff,
            new { name = "Nobody Presents", owner_handle = "nobody_here" }), HttpStatusCode.UnprocessableEntity);
        Assert.Contains("owner_handle", unknown.GetRawText(), StringComparison.Ordinal);

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/organizations", owner), HttpStatusCode.OK);
        var only = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal(("Lakeside Presents", "owner"), (only.GetProperty("name").GetString(), only.GetProperty("my_role").GetString()));
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}", owner), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}", stranger)).StatusCode);
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/organizations", stranger), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Owners_manage_members_roles_are_enforced_and_every_change_is_audited()
    {
        var (id, owner) = await OrganizationAsync("Harbor Live", "harbor_owner");
        var ada = await SignInAsync("ada_admin");
        var ana = await SignInAsync("ana_analyst");

        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/ada_admin", owner, new { role = "admin" }), HttpStatusCode.OK);
        var team = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/ana_analyst", owner, new { role = "analyst" }), HttpStatusCode.OK);
        Assert.Equal([("harbor_owner", "owner"), ("ada_admin", "admin"), ("ana_analyst", "analyst")], Members(team));

        // Only owners manage members; any member reads the organization.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/ana_analyst", ada, new { role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/v1/organizations/{id}/members/ada_admin", ana)).StatusCode);
        Assert.Equal("analyst", (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}", ana), HttpStatusCode.OK))
            .GetProperty("my_role").GetString());
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/ada_admin", owner, new { role = "boss" }), HttpStatusCode.UnprocessableEntity);

        // An organization keeps an owner.
        var lastOwner = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/harbor_owner", owner, new { role = "admin" }), HttpStatusCode.Conflict);
        Assert.Contains("must keep an owner", lastOwner.GetProperty("detail").GetString(), StringComparison.Ordinal);
        await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/organizations/{id}/members/harbor_owner", owner), HttpStatusCode.Conflict);

        // Hand ownership over, step down, remove someone.
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/ada_admin", owner, new { role = "owner" }), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/harbor_owner", owner, new { role = "admin" }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/organizations/{id}/members/ana_analyst", ada)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/v1/organizations/{id}/members/ana_analyst", ada)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}", ana)).StatusCode);

        await using var connection = await _db.OpenConnectionAsync();
        var audit = (await connection.QueryAsync<(string Action, string Subject, string? Actor, string? OldRole, string? NewRole)>(
            """
            SELECT a.action, s.handle::text, act.handle::text, a.old_role, a.new_role
            FROM organization_audit a JOIN users s ON s.id = a.user_id LEFT JOIN users act ON act.id = a.actor_id
            WHERE a.organization_id = @id::uuid ORDER BY a.id
            """,
            new { id })).ToList();
        Assert.Equal(
        [
            ("member_added", "harbor_owner", "org_staff_harbor_owner", null, "owner"),
            ("member_added", "ada_admin", "harbor_owner", null, "admin"),
            ("member_added", "ana_analyst", "harbor_owner", null, "analyst"),
            ("role_changed", "ada_admin", "harbor_owner", "admin", "owner"),
            ("role_changed", "harbor_owner", "harbor_owner", "owner", "admin"),
            ("member_removed", "ana_analyst", "ada_admin", "analyst", null),
        ], audit);
    }

    [Fact]
    public async Task A_promoter_runs_their_own_show_from_creation_to_the_draw_and_sees_fans_as_handles_only()
    {
        var (id, owner) = await OrganizationAsync("Metro Shows", "metro_owner");
        var analyst = await SignInAsync("metro_analyst");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/metro_analyst", owner, new { role = "analyst" }), HttpStatusCode.OK);

        var show = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{id}/events", owner, new
        {
            artist_id = _artistId, name = "Lakeside at Metro", venue = "Metro", city_id = "us-il-chicago",
            starts_at = DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
        }), HttpStatusCode.Created);
        var eventId = show.GetProperty("event").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/v1/organizations/{id}/events", analyst, new
        {
            artist_id = _artistId, name = "Not allowed", city_id = "us-il-chicago", starts_at = DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
        })).StatusCode);

        var allocation = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{id}/events/{eventId}/allocations", owner, new
        {
            tickets = 1, face_value_cents = 4500, min_raw_score = 0,
            registration_opens_at = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
            registration_closes_at = DateTimeOffset.UtcNow.AddDays(3).ToString("O"),
        }), HttpStatusCode.Created);
        var allocationId = allocation.GetProperty("id").GetString();
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{id}/allocations/{allocationId}/publish", owner), HttpStatusCode.OK);

        var listed = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}/events", analyst), HttpStatusCode.OK);
        Assert.Equal(eventId, Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("event").GetProperty("id").GetString());

        // Two fans register; the higher score gets the one seat.
        foreach (var (handle, score) in new[] { ("metro_fan_a", 80m), ("metro_fan_b", 60m) })
        {
            var fan = await FanAsync(handle, score);
            await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", fan), HttpStatusCode.Created);
        }

        var before = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}/allocations/{allocationId}/registrations", analyst), HttpStatusCode.OK);
        var first = before.GetProperty("items")[0];
        Assert.Equal(("registered", true), (first.GetProperty("status").GetString(), first.GetProperty("local").GetBoolean()));
        foreach (var hidden in new[] { "raw_score", "percentile", "hub_city_ids", "email" })
        {
            Assert.False(first.TryGetProperty(hidden, out _), $"promoters must not see {hidden}");
        }

        await CloseRegistrationAsync(allocationId!);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/v1/organizations/{id}/allocations/{allocationId}/selection", analyst)).StatusCode);
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{id}/allocations/{allocationId}/selection", owner), HttpStatusCode.Accepted);
        await Draws.RunQueuedAsync(_db);

        var draw = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}/allocations/{allocationId}/selection", analyst), HttpStatusCode.OK);
        Assert.Equal("done", draw.GetProperty("status").GetString());
        var after = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}/allocations/{allocationId}/registrations", analyst), HttpStatusCode.OK);
        Assert.Equal([("metro_fan_a", "selected"), ("metro_fan_b", "not_selected")],
            after.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("handle").GetString(), i.GetProperty("status").GetString())));
        var seats = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}/allocations/{allocationId}/reservations", analyst), HttpStatusCode.OK);
        Assert.Equal("metro_fan_a", Assert.Single(seats.GetProperty("items").EnumerateArray()).GetProperty("handle").GetString());
    }

    [Fact]
    public async Task No_organization_reaches_another_organizations_shows_or_staff_managed_ones()
    {
        var (mine, owner) = await OrganizationAsync("North Stage", "north_owner");
        var (theirs, rival) = await OrganizationAsync("South Stage", "south_owner");
        var staff = await StaffAsync("route_staff");

        var theirShow = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{theirs}/events", rival, ShowBody("South show")), HttpStatusCode.Created);
        var theirEvent = theirShow.GetProperty("event").GetProperty("id").GetString();
        var theirAllocation = (await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/organizations/{theirs}/events/{theirEvent}/allocations", rival,
            AllocationBody()), HttpStatusCode.Created)).GetProperty("id").GetString();
        var staffEvent = (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/events", staff, ShowBody("Staff show")), HttpStatusCode.Created))
            .GetProperty("event").GetProperty("id").GetString();

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, $"/v1/organizations/{mine}/events/{theirEvent}"),
                     (HttpMethod.Patch, $"/v1/organizations/{mine}/events/{theirEvent}"),
                     (HttpMethod.Get, $"/v1/organizations/{mine}/events/{staffEvent}"),
                     (HttpMethod.Post, $"/v1/organizations/{mine}/events/{theirEvent}/allocations"),
                     (HttpMethod.Post, $"/v1/organizations/{mine}/allocations/{theirAllocation}/publish"),
                     (HttpMethod.Get, $"/v1/organizations/{mine}/allocations/{theirAllocation}/registrations"),
                     (HttpMethod.Get, $"/v1/organizations/{theirs}"),
                     (HttpMethod.Get, $"/v1/organizations/{theirs}/events"),
                 })
        {
            var body = method == HttpMethod.Patch ? new { name = "Taken over" } : method == HttpMethod.Post ? (object)AllocationBody() : null;
            Assert.True((await SendAsync(method, path, owner, body)).StatusCode == HttpStatusCode.NotFound, $"{method} {path} must be not found");
        }

        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{mine}/events", owner), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray());

        // Staff can create a show on an organization's behalf; it then belongs to that organization.
        var handedOver = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/events", staff,
            new Dictionary<string, object?>(ShowBody("Booked by staff")) { ["organization_id"] = mine }), HttpStatusCode.Created);
        var handedId = handedOver.GetProperty("event").GetProperty("id").GetString();
        Assert.Equal(handedId, Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{mine}/events", owner), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray()).GetProperty("event").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Deleting_a_members_account_ends_the_membership()
    {
        var (id, owner) = await OrganizationAsync("Gone Soon", "gone_owner");
        var leaver = await SignInAsync("leaving_admin");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/organizations/{id}/members/leaving_admin", owner, new { role = "owner" }), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Delete, "/v1/me", leaver)).StatusCode);

        var team = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/organizations/{id}", owner), HttpStatusCode.OK);
        Assert.Equal([("gone_owner", "owner")], Members(team));
        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal("account_deleted", await connection.ExecuteScalarAsync<string>(
            "SELECT action FROM organization_audit WHERE organization_id = @id::uuid ORDER BY id DESC LIMIT 1", new { id }));
    }

    // ------------------------------------------------------------------ helpers

    private Dictionary<string, object?> ShowBody(string name) => new()
    {
        ["artist_id"] = _artistId, ["name"] = name, ["venue"] = "Hall", ["city_id"] = "us-il-chicago",
        ["starts_at"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
    };

    private static object AllocationBody() => new
    {
        tickets = 10, face_value_cents = 4500, min_raw_score = 0,
        registration_opens_at = DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
        registration_closes_at = DateTimeOffset.UtcNow.AddDays(3).ToString("O"),
    };

    private async Task<(string Id, string OwnerToken)> OrganizationAsync(string name, string ownerHandle)
    {
        var staff = await StaffAsync($"org_staff_{ownerHandle}"[..Math.Min(30, 10 + ownerHandle.Length)]);
        var owner = await SignInAsync(ownerHandle);
        var created = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/organizations", staff,
            new { name, owner_handle = ownerHandle }), HttpStatusCode.Created);
        return (created.GetProperty("id").GetString()!, owner);
    }

    private static List<(string?, string?)> Members(JsonElement organization) =>
        [.. organization.GetProperty("members").EnumerateArray().Select(m => (m.GetProperty("handle").GetString(), m.GetProperty("role").GetString()))];

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

    /// <summary>A fan in Chicago with a score for the artist.</summary>
    private async Task<string> FanAsync(string handle, decimal score)
    {
        var token = await SignInAsync(handle);
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token, new { city_ids = Chicago }), HttpStatusCode.OK);
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
            SELECT id, @_artistId, @score, @score, false, 0.5 FROM users WHERE handle = @handle
            """,
            new { _artistId, score, handle });
        return token;
    }

    private async Task CloseRegistrationAsync(string allocationId)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
            UPDATE allocations SET registration_closes_at = now() - interval '1 minute' WHERE id = @allocationId::uuid;
            ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
            """,
            new { allocationId });
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
