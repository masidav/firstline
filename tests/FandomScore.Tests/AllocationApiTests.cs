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

/// <summary>Shows and fan allocations: staff management under /admin and the fan views (migration 0011).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AllocationApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private string _staff = null!;
    private string _fan = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"allocations_{Guid.NewGuid():N}");
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
        _staff = await SignInAsync("staffer");
        _fan = await SignInAsync("fan_one");
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = 'staffer'");
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Staff_add_a_show_and_a_draft_that_fans_see_only_once_published()
    {
        var created = await JsonAsync(await CreateShowAsync(), HttpStatusCode.Created);
        var show = created.GetProperty("event");
        var eventId = show.GetProperty("id").GetString()!;
        Assert.Equal(("us-il-chicago", "Chicago", "IL", "US"),
            (show.GetProperty("city_id").GetString(), show.GetProperty("city").GetString(), show.GetProperty("region").GetString(),
             show.GetProperty("country").GetString()));
        Assert.False(show.GetProperty("tickets_available").GetBoolean());

        var draft = await JsonAsync(await CreateAllocationAsync(eventId), HttpStatusCode.Created);
        var allocationId = draft.GetProperty("id").GetString()!;
        Assert.Equal("draft", draft.GetProperty("status").GetString());
        Assert.Equal(("Fan allocation", 100, 4500, "USD", 70m),
            (draft.GetProperty("name").GetString(), draft.GetProperty("tickets").GetInt32(), draft.GetProperty("face_value_cents").GetInt32(),
             draft.GetProperty("currency").GetString(), draft.GetProperty("min_percentile").GetDecimal()));

        var fanView = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/events/{eventId}", _fan), HttpStatusCode.OK);
        Assert.Empty(fanView.GetProperty("allocations").EnumerateArray());
        var staffView = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/events/{eventId}", _staff), HttpStatusCode.OK);
        Assert.Single(staffView.GetProperty("allocations").EnumerateArray());

        var published = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/publish", _staff), HttpStatusCode.OK);
        Assert.Equal("upcoming", published.GetProperty("status").GetString());
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/publish", _staff), HttpStatusCode.OK);

        fanView = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/events/{eventId}", _fan), HttpStatusCode.OK);
        Assert.Equal("upcoming", Assert.Single(fanView.GetProperty("allocations").EnumerateArray()).GetProperty("status").GetString());
        var events = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{_artistId}/events", _fan), HttpStatusCode.OK);
        Assert.True(Assert.Single(events.GetProperty("items").EnumerateArray()).GetProperty("tickets_available").GetBoolean());
    }

    [Fact]
    public async Task Fans_cannot_manage_shows()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateShowAsync(_fan)).StatusCode);
    }

    [Fact]
    public async Task Rules_can_change_until_registration_opens_then_lock_but_can_be_cancelled()
    {
        var eventId = await ShowIdAsync();
        var allocationId = (await JsonAsync(await CreateAllocationAsync(eventId), HttpStatusCode.Created)).GetProperty("id").GetString()!;
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/publish", _staff), HttpStatusCode.OK);

        var changed = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/allocations/{allocationId}", _staff,
            new { tickets = 120, min_percentile = 80.5 }), HttpStatusCode.OK);
        Assert.Equal((120, 80.5m), (changed.GetProperty("tickets").GetInt32(), changed.GetProperty("min_percentile").GetDecimal()));

        await OpenRegistrationAsync(allocationId);
        Assert.Equal("open", (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/events/{eventId}", _fan), HttpStatusCode.OK))
            .GetProperty("allocations")[0].GetProperty("status").GetString());

        var locked = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/allocations/{allocationId}", _staff,
            new { tickets = 10 }), HttpStatusCode.Conflict);
        Assert.Equal("allocation_locked", locked.GetProperty("code").GetString());
        var moved = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/events/{eventId}", _staff,
            new { city_id = "ca-on-toronto" }), HttpStatusCode.Conflict);
        Assert.Equal("allocation_locked", moved.GetProperty("code").GetString());
        // The name and venue of the show can still change.
        await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/events/{eventId}", _staff, new { venue = "Metro" }), HttpStatusCode.OK);

        var deleteOpen = await SendAsync(HttpMethod.Delete, $"/v1/admin/allocations/{allocationId}", _staff);
        Assert.Equal(HttpStatusCode.Conflict, deleteOpen.StatusCode);
        var cancelled = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/cancel", _staff), HttpStatusCode.OK);
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        var events = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{_artistId}/events", _fan), HttpStatusCode.OK);
        Assert.False(events.GetProperty("items")[0].GetProperty("tickets_available").GetBoolean());
    }

    [Fact]
    public async Task Drafts_are_deleted_not_cancelled()
    {
        var eventId = await ShowIdAsync();
        var allocationId = (await JsonAsync(await CreateAllocationAsync(eventId), HttpStatusCode.Created)).GetProperty("id").GetString()!;

        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/cancel", _staff), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/admin/allocations/{allocationId}", _staff)).StatusCode);
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/events/{eventId}", _staff), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/publish", _staff)).StatusCode);
    }

    [Fact]
    public async Task Publishing_needs_an_active_artist()
    {
        Guid onboarding;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            onboarding = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO artists (name, spotify_artist_id) VALUES ('New Band', 'sp-new') RETURNING id");
        }

        var eventId = (await JsonAsync(await CreateShowAsync(artistId: onboarding), HttpStatusCode.Created))
            .GetProperty("event").GetProperty("id").GetString()!;
        var allocationId = (await JsonAsync(await CreateAllocationAsync(eventId), HttpStatusCode.Created)).GetProperty("id").GetString()!;

        var problem = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/publish", _staff),
            HttpStatusCode.Conflict);
        Assert.Contains("active", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_must_close_before_the_show()
    {
        var eventId = await ShowIdAsync();

        var problem = await JsonAsync(await CreateAllocationAsync(eventId, closesInDays: 30), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("registration_closes_at", problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Theory]
    [InlineData("""{"name": "Show", "city_id": "us-il-chicago", "starts_at": "2030-01-01T20:00:00Z"}""", "artist_id")]
    [InlineData("""{"artist_id": "ARTIST", "name": "Show", "city_id": "us-xx-atlantis", "starts_at": "2030-01-01T20:00:00Z"}""", "city_id")]
    [InlineData("""{"artist_id": "ARTIST", "name": "Show", "city_id": "us-il-chicago", "starts_at": "next friday"}""", "starts_at")]
    [InlineData("""{"artist_id": "ARTIST", "name": "Show", "city_id": "us-il-chicago", "starts_at": "2030-01-01T20:00:00"}""", "starts_at")]
    [InlineData("""{"artist_id": "ARTIST", "name": "", "city_id": "us-il-chicago", "starts_at": "2030-01-01T20:00:00Z"}""", "name")]
    [InlineData("""{"artist_id": "00000000-0000-0000-0000-000000000000", "name": "Show", "city_id": "us-il-chicago", "starts_at": "2030-01-01T20:00:00Z"}""", "artist_id")]
    public async Task Shows_are_validated(string body, string field)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/events")
        {
            Content = new StringContent(body.Replace("ARTIST", _artistId.ToString(), StringComparison.Ordinal),
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _staff);

        var problem = await JsonAsync(await _client.SendAsync(request), HttpStatusCode.UnprocessableEntity);

        Assert.Equal(field, problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Theory]
    [InlineData("""{"tickets": 0, "face_value_cents": 100}""", "tickets")]
    [InlineData("""{"tickets": 10, "face_value_cents": -1}""", "face_value_cents")]
    [InlineData("""{"tickets": 10, "face_value_cents": 100, "min_percentile": 120}""", "min_percentile")]
    [InlineData("""{"tickets": 10, "face_value_cents": 100, "currency": "usd"}""", "currency")]
    public async Task Allocations_are_validated(string partial, string field)
    {
        var eventId = await ShowIdAsync();
        var times = $"\"registration_opens_at\": \"{DateTime.UtcNow.AddDays(1):O}\", \"registration_closes_at\": \"{DateTime.UtcNow.AddDays(5):O}\"";
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/admin/events/{eventId}/allocations")
        {
            Content = new StringContent(partial.TrimEnd('}') + ", " + times + "}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _staff);

        var problem = await JsonAsync(await _client.SendAsync(request), HttpStatusCode.UnprocessableEntity);

        Assert.Equal(field, problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Past_shows_are_listed_only_on_request()
    {
        var eventId = await ShowIdAsync();
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE events SET starts_at = now() - interval '1 day' WHERE id = @id::uuid", new { id = eventId });
        }

        var upcoming = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{_artistId}/events", _fan), HttpStatusCode.OK);
        var all = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{_artistId}/events?include_past=true", _fan), HttpStatusCode.OK);

        Assert.Empty(upcoming.GetProperty("items").EnumerateArray());
        Assert.Equal(eventId, Assert.Single(all.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Staff_still_see_and_edit_a_paused_artists_show_that_fans_no_longer_see()
    {
        var eventId = await ShowIdAsync();
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE artists SET status = 'paused' WHERE id = @_artistId", new { _artistId });
        }

        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/events/{eventId}", _fan), HttpStatusCode.NotFound);
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/events/{eventId}", _staff), HttpStatusCode.OK);
        var renamed = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/events/{eventId}", _staff, new { name = "Renamed" }),
            HttpStatusCode.OK);
        Assert.Equal("Renamed", renamed.GetProperty("event").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Changing_the_window_so_registration_closes_before_it_opens_is_refused()
    {
        var eventId = await ShowIdAsync();
        var allocationId = (await JsonAsync(await CreateAllocationAsync(eventId), HttpStatusCode.Created)).GetProperty("id").GetString()!;

        var problem = await JsonAsync(await SendAsync(HttpMethod.Patch, $"/v1/admin/allocations/{allocationId}", _staff,
            new { registration_closes_at = DateTimeOffset.UtcNow.AddHours(1) }), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("Registration must close after it opens.", problem.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Unknown_shows_and_artists_are_not_found()
    {
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/events/{Guid.NewGuid()}", _fan), HttpStatusCode.NotFound);
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/artists/{Guid.NewGuid()}/events", _fan), HttpStatusCode.NotFound);
    }

    private Task<HttpResponseMessage> CreateShowAsync(string? token = null, Guid? artistId = null) =>
        SendAsync(HttpMethod.Post, "/v1/admin/events", token ?? _staff, new
        {
            artist_id = artistId ?? _artistId,
            name = "Geese at the Metro",
            venue = (string?)null,
            city_id = "us-il-chicago",
            starts_at = DateTimeOffset.UtcNow.AddDays(14).ToOffset(TimeSpan.FromHours(-6)),
        });

    private async Task<string> ShowIdAsync() =>
        (await JsonAsync(await CreateShowAsync(), HttpStatusCode.Created)).GetProperty("event").GetProperty("id").GetString()!;

    private Task<HttpResponseMessage> CreateAllocationAsync(string eventId, int closesInDays = 5) =>
        SendAsync(HttpMethod.Post, $"/v1/admin/events/{eventId}/allocations", _staff, new
        {
            tickets = 100,
            face_value_cents = 4500,
            min_percentile = 70,
            registration_opens_at = DateTimeOffset.UtcNow.AddDays(1),
            registration_closes_at = DateTimeOffset.UtcNow.AddDays(closesInDays),
        });

    // Moves the window into the past directly: through the API, rules can't change once open, by design.
    private async Task OpenRegistrationAsync(string allocationId)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE allocations SET registration_opens_at = now() - interval '1 minute' WHERE id = @id::uuid", new { id = allocationId });
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
