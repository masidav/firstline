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
/// Reservations (migration 0020): the draw hands selected fans a reservation, the ticket token is delivered late
/// and only to its holder, and a returned reservation goes to the next fan in the ranking.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReservationApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] Chicago = ["us-il-chicago"];

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private Guid _eventId;
    private Guid _allocationId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"reservations_{Guid.NewGuid():N}");
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
        await using var connection = await _db.OpenConnectionAsync();
        _eventId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, venue, city_id, starts_at) VALUES (@_artistId, 'Geese at Metro', 'Metro', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
        // Three seats; tickets delivered 36 hours before the show.
        _allocationId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at,
                                     published_at, delivery_hours_before_show)
            VALUES (@_eventId, 3, 4500, 10, now() - interval '1 hour', now() + interval '2 seconds', now(), 36) RETURNING id
            """,
            new { _eventId });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task The_draw_hands_out_reservations_delivery_reveals_the_token_to_the_holder_only_and_a_return_goes_down_the_ranking()
    {
        var ana = await FanAsync("ana", 90, plusOne: true);   // 2 seats
        var ben = await FanAsync("ben", 80);                  // 1 seat
        var cat = await FanAsync("cat", 70);                  // not selected
        var dee = await FanAsync("dee", 60);                  // not selected
        var guest = await SignInAsync("gus");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration/guest", ana, new { handle = "gus" }), HttpStatusCode.OK);
        await RunDrawAsync();

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", ana), HttpStatusCode.OK);
        var held = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal(("held", "holder", 2, 4500, "gus", true), (held.GetProperty("status").GetString(), held.GetProperty("role").GetString(),
            held.GetProperty("seats").GetInt32(), held.GetProperty("face_value_cents").GetInt32(), held.GetProperty("guest_handle").GetString(),
            held.GetProperty("can_return").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, held.GetProperty("ticket").ValueKind);
        Assert.Equal("Geese at Metro", held.GetProperty("event").GetProperty("name").GetString());
        Assert.Equal("Geese", held.GetProperty("artist").GetProperty("name").GetString());
        var reservationId = held.GetProperty("id").GetString()!;
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", cat), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());

        // The registration says what the fan holds.
        var registration = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{_allocationId}/eligibility", ana), HttpStatusCode.OK);
        Assert.Equal((reservationId, "held", "selected"), (registration.GetProperty("registration").GetProperty("reservation_id").GetString(),
            registration.GetProperty("registration").GetProperty("reservation_status").GetString(),
            registration.GetProperty("registration").GetProperty("status").GetString()));

        // Delivery time arrives: the holder gets the token, the guest sees the reservation without it, strangers 404.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("ALTER TABLE reservations DISABLE TRIGGER trg_reservation_guard; UPDATE reservations SET deliver_at = now() - interval '1 minute'; ALTER TABLE reservations ENABLE TRIGGER trg_reservation_guard;");
        }

        var delivered = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/reservations/{reservationId}", ana), HttpStatusCode.OK);
        Assert.Equal("delivered", delivered.GetProperty("status").GetString());
        var ticket = delivered.GetProperty("ticket");
        Assert.Matches("^[0-9a-f]{64}$", ticket.GetProperty("token").GetString());
        Assert.Equal(("ana", "gus"), (ticket.GetProperty("holder_handle").GetString(), ticket.GetProperty("guest_handle").GetString()));
        var asGuest = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/reservations/{reservationId}", guest), HttpStatusCode.OK);
        Assert.Equal(("guest", JsonValueKind.Null, false), (asGuest.GetProperty("role").GetString(), asGuest.GetProperty("ticket").ValueKind,
            asGuest.GetProperty("can_return").GetBoolean()));
        Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", guest), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/reservations/{reservationId}", cat)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/v1/reservations/{reservationId}/return", guest)).StatusCode);

        // Ana can't go: both seats go to Cat and Dee, the next two in the ranking.
        var returned = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/reservations/{reservationId}/return", ana), HttpStatusCode.OK);
        Assert.Equal(("returned", false, JsonValueKind.Null), (returned.GetProperty("status").GetString(), returned.GetProperty("can_return").GetBoolean(),
            returned.GetProperty("ticket").ValueKind));
        Assert.Equal("returned", (await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/reservations/{reservationId}/return", ana), HttpStatusCode.OK))
            .GetProperty("status").GetString());
        var anaRegistration = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", ana), HttpStatusCode.OK)).GetProperty("items")[0]
            .GetProperty("registration");
        Assert.Equal("returned", anaRegistration.GetProperty("status").GetString());
        foreach (var fan in new[] { cat, dee })
        {
            var item = Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", fan), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
            Assert.Equal(("held", 1), (item.GetProperty("status").GetString(), item.GetProperty("seats").GetInt32()));
        }

        // Ben returns too: nobody is left in the ranking, so a seat stays free; he can't add a +1 to what he gave back.
        var benId = Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", ben), HttpStatusCode.OK)).GetProperty("items").EnumerateArray())
            .GetProperty("id").GetString();
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/reservations/{benId}/return", ben), HttpStatusCode.OK);
        var plusOne = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", ben, new { plus_one = true }), HttpStatusCode.Conflict);
        Assert.Equal("plus_one_unavailable", plusOne.GetProperty("code").GetString());

        // Staff see holders in ranking order, never tokens.
        var staff = await StaffAsync("staffer");
        var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/allocations/{_allocationId}/reservations", staff), HttpStatusCode.OK);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["ana", "ben", "cat", "dee"], items.Select(i => i.GetProperty("handle").GetString()));
        Assert.All(items, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("reservation").GetProperty("ticket").ValueKind));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, $"/v1/admin/allocations/{_allocationId}/reservations", ben)).StatusCode);
        Assert.Equal(1, (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/events/{_eventId}", ben), HttpStatusCode.OK))
            .GetProperty("allocations")[0].GetProperty("seats_left").GetInt32());
    }

    [Fact]
    public async Task Deleting_an_account_cancels_its_reservation_and_hands_the_seat_on()
    {
        var ana = await FanAsync("ana", 90);
        await FanAsync("ben", 80);
        await FanAsync("cat", 70);
        var dee = await FanAsync("dee", 60);
        await RunDrawAsync();
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", dee), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Delete, "/v1/me", ana)).StatusCode);

        var deeItem = Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", dee), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        Assert.Equal("held", deeItem.GetProperty("status").GetString());
        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal("account_deleted", await connection.ExecuteScalarAsync<string>(
            "SELECT cancelled_reason FROM reservations res JOIN allocation_registrations r ON r.id = res.registration_id WHERE r.withdrawn_reason = 'account_deleted'"));
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT seats_assigned FROM allocation_selections WHERE allocation_id = @_allocationId", new { _allocationId }));
    }

    [Fact]
    public async Task The_delivery_rule_is_part_of_the_allocation_and_validated()
    {
        var staff = await StaffAsync("staffer");
        var created = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/events/{_eventId}/allocations", staff, new
        {
            tickets = 5, face_value_cents = 3000, registration_opens_at = DateTime.UtcNow.AddDays(1).ToString("o"),
            registration_closes_at = DateTime.UtcNow.AddDays(3).ToString("o"), delivery_hours_before_show = 6,
        }), HttpStatusCode.Created);
        Assert.Equal(6, created.GetProperty("delivery_hours_before_show").GetInt32());
        var id = created.GetProperty("id").GetString();
        var updated = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/allocations/{id}", staff, new { delivery_hours_before_show = 2 }), HttpStatusCode.OK);
        Assert.Equal(2, updated.GetProperty("delivery_hours_before_show").GetInt32());
        var invalid = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/allocations/{id}", staff, new { delivery_hours_before_show = -1 }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("delivery_hours_before_show", invalid.GetProperty("errors")[0].GetProperty("field").GetString());
        // The default is a day; once registration has opened it is locked with the other rules.
        Assert.Equal(36, (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/events/{_eventId}", staff), HttpStatusCode.OK))
            .GetProperty("allocations")[0].GetProperty("delivery_hours_before_show").GetInt32());
        var locked = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/allocations/{_allocationId}", staff, new { delivery_hours_before_show = 1 }), HttpStatusCode.Conflict);
        Assert.Equal("allocation_locked", locked.GetProperty("code").GetString());
    }

    private async Task RunDrawAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5));   // registration closes
        var staff = await StaffAsync("drawer");
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{_allocationId}/selection", staff), HttpStatusCode.Accepted);
        await Draws.RunQueuedAsync(_db);
    }

    private async Task<string> FanAsync(string handle, decimal score, bool plusOne = false)
    {
        var token = await SignInAsync(handle);
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token, new { city_ids = Chicago }), HttpStatusCode.OK);
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                SELECT id, @_artistId, @score, least(@score, 100), false, 0.1 FROM users WHERE handle = @handle
                """,
                new { _artistId, score, handle });
        }

        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{_allocationId}/registration", token, new { plus_one = plusOne }), HttpStatusCode.Created);
        return token;
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
