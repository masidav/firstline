using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Spotify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>/me/spotify against a real database, with Spotify faked.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SpotifyApiTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private static readonly string Verifier = new('v', 43);

    private readonly ConcurrentQueue<string> _logs = new();
    private readonly FakeSpotify _spotify = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"spotify_api_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        var options = FakeSpotify.Options();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Spotify:ClientId", options.ClientId);
            builder.UseSetting("Spotify:RedirectUris", options.RedirectUris);
            builder.UseSetting("Spotify:TokenKey", options.TokenKey);
            builder.UseSetting("Logging:LogLevel:Default", "Debug");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_logs)));
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>());
                services.AddHttpClient<SpotifyClient>().ConfigurePrimaryHttpMessageHandler(() => _spotify);
            });
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public void Dispose() => _spotify.Dispose();

    [Fact]
    public async Task Without_a_connection_status_says_not_connected()
    {
        var token = await SignInAsync("fan_one");

        var status = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/spotify", token), HttpStatusCode.OK);
        Assert.False(status.GetProperty("connected").GetBoolean());
        Assert.Equal("idle", status.GetProperty("sync_state").GetString());

        var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", token), HttpStatusCode.OK);
        Assert.False(me.GetProperty("spotify").GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Connecting_exchanges_the_code_stores_the_token_encrypted_and_queues_a_sync()
    {
        var token = await SignInAsync("fan_one");

        var status = await JsonAsync(await ConnectAsync(token), HttpStatusCode.OK);

        Assert.True(status.GetProperty("connected").GetBoolean());
        Assert.Equal("queued", status.GetProperty("sync_state").GetString());
        Assert.Equal(SpotifyOptions.RequiredScopes, status.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));

        var exchange = Assert.Single(_spotify.Calls, c => c.Path == "token").Form;
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("code-1", exchange["code"]);
        Assert.Equal(Verifier, exchange["code_verifier"]);
        Assert.Equal(FakeSpotify.RedirectUri, exchange["redirect_uri"]);
        Assert.Equal(FakeSpotify.ClientId, exchange["client_id"]);
        Assert.False(exchange.ContainsKey("client_secret"));
        Assert.Equal("access-1", Assert.Single(_spotify.Calls, c => c.Path == "me").Bearer);

        await using var connection = await _db.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<(Guid UserId, string SpotifyUserId, byte[] Token)>(
            "SELECT user_id, spotify_user_id, refresh_token_enc FROM spotify_connections");
        Assert.Equal("sp-user-1", row.SpotifyUserId);
        Assert.Equal("refresh-1", new SpotifyTokenProtector(FakeSpotify.Options()).Unprotect(row.UserId, row.Token));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM jobs WHERE kind = 'spotify.sync' AND payload->>'user_id' = @id", new { id = row.UserId.ToString() }));

        var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", token), HttpStatusCode.OK);
        Assert.True(me.GetProperty("spotify").GetProperty("connected").GetBoolean());

        // Nothing secret reaches the logs.
        Assert.DoesNotContain(_logs, line => line.Contains("refresh-1", StringComparison.Ordinal)
                                             || line.Contains("access-1", StringComparison.Ordinal)
                                             || line.Contains("code-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bad_requests_are_rejected_before_calling_spotify()
    {
        var token = await SignInAsync("fan_one");

        var problem = await JsonAsync(await ConnectAsync(token, redirectUri: "https://evil.example/callback", verifier: "short"),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.Equal(["code_verifier", "redirect_uri"],
            problem.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()));
        Assert.Empty(_spotify.Calls);
    }

    [Fact]
    public async Task Rejected_codes_missing_scopes_and_unlisted_accounts_are_422()
    {
        var token = await SignInAsync("fan_one");

        _spotify.TokenStatus = HttpStatusCode.BadRequest;
        _spotify.TokenResponse = new { error = "invalid_grant", error_description = "Invalid authorization code" };
        await AssertCodeRejectedAsync(await ConnectAsync(token));

        _spotify.TokenStatus = HttpStatusCode.OK;
        _spotify.TokenResponse = FakeSpotify.Tokens("refresh-1", scope: "user-top-read");
        var missing = await AssertCodeRejectedAsync(await ConnectAsync(token));
        Assert.Contains("user-library-read", missing, StringComparison.Ordinal);

        // Development mode: a Spotify account not on the app's allowlist.
        _spotify.TokenResponse = FakeSpotify.Tokens("refresh-1");
        _spotify.ApiStatus = HttpStatusCode.Forbidden;
        Assert.Contains("invited", await AssertCodeRejectedAsync(await ConnectAsync(token)), StringComparison.Ordinal);

        var status = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/spotify", token), HttpStatusCode.OK);
        Assert.False(status.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Spotify_outages_are_503()
    {
        var token = await SignInAsync("fan_one");
        _spotify.TokenStatus = HttpStatusCode.BadGateway;

        var problem = await JsonAsync(await ConnectAsync(token), HttpStatusCode.ServiceUnavailable);

        Assert.Equal("spotify_unavailable", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task One_spotify_account_connects_to_one_user()
    {
        var first = await SignInAsync("fan_one");
        var second = await SignInAsync("fan_two");
        await JsonAsync(await ConnectAsync(first), HttpStatusCode.OK);

        var problem = await JsonAsync(await ConnectAsync(second), HttpStatusCode.Conflict);

        Assert.Equal("spotify_in_use", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Switching_spotify_accounts_drops_the_old_accounts_data()
    {
        var token = await SignInAsync("fan_one");
        await JsonAsync(await ConnectAsync(token), HttpStatusCode.OK);
        var userId = await SeedSignalsAsync();

        await JsonAsync(await ConnectAsync(token), HttpStatusCode.OK);
        Assert.Equal(1, await CountSignalsAsync(userId));

        _spotify.UserId = "sp-user-2";
        await JsonAsync(await ConnectAsync(token), HttpStatusCode.OK);
        Assert.Equal(0, await CountSignalsAsync(userId));
    }

    [Fact]
    public async Task Disconnecting_deletes_the_connection_and_synced_data()
    {
        var token = await SignInAsync("fan_one");
        await JsonAsync(await ConnectAsync(token), HttpStatusCode.OK);
        var userId = await SeedSignalsAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/v1/me/spotify", token)).StatusCode);

        Assert.Equal(0, await CountSignalsAsync(userId));
        var status = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/spotify", token), HttpStatusCode.OK);
        Assert.False(status.GetProperty("connected").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/v1/me/spotify", token)).StatusCode);
    }

    [Fact]
    public async Task Manual_sync_is_queued_once_and_limited_to_one_per_15_minutes()
    {
        var token = await SignInAsync("fan_one");
        var notConnected = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync", token), HttpStatusCode.Conflict);
        Assert.Equal("spotify_not_connected", notConnected.GetProperty("code").GetString());

        await JsonAsync(await ConnectAsync(token), HttpStatusCode.OK);

        // The first sync is still queued: same job.
        var queued = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync", token), HttpStatusCode.Accepted);
        Assert.Equal("queued", queued.GetProperty("sync_state").GetString());
        Assert.Equal(1, await CountJobsAsync());

        // Finished just now: too soon.
        await ExecuteAsync("UPDATE spotify_connections SET sync_state = 'idle'; UPDATE jobs SET status = 'succeeded', finished_at = now()");
        var limited = await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync", token);
        var problem = await JsonAsync(limited, HttpStatusCode.TooManyRequests);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        Assert.InRange(int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture), 880, 900);

        await ExecuteAsync("UPDATE spotify_connections SET sync_requested_at = now() - interval '16 minutes'");
        var again = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync", token), HttpStatusCode.Accepted);
        Assert.Equal("queued", again.GetProperty("sync_state").GetString());
        Assert.Equal(2, await CountJobsAsync());

        await ExecuteAsync("UPDATE spotify_connections SET sync_state = 'reauth_required', sync_requested_at = NULL");
        var reauth = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync", token), HttpStatusCode.Conflict);
        Assert.Equal("spotify_reauth_required", reauth.GetProperty("code").GetString());

        // Reconnecting clears it.
        var reconnected = await JsonAsync(await ConnectAsync(token), HttpStatusCode.OK);
        Assert.Equal("queued", reconnected.GetProperty("sync_state").GetString());
    }

    [Fact]
    public async Task The_background_sync_on_opening_queues_only_when_the_last_sync_is_old_and_never_errors()
    {
        var token = await SignInAsync("fan_one");
        await JsonAsync(await ConnectAsync(token), HttpStatusCode.OK);   // connecting queues the first sync

        // Just connected (a sync was asked for a moment ago and is queued): the status, nothing new.
        Assert.Equal("queued", (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync?background=true", token), HttpStatusCode.OK))
            .GetProperty("sync_state").GetString());
        Assert.Equal(1, await CountJobsAsync());

        // Synced a minute ago: nothing to do, no error.
        await ExecuteAsync("UPDATE spotify_connections SET sync_state = 'idle', last_synced_at = now() - interval '1 minute', sync_requested_at = NULL; UPDATE jobs SET status = 'succeeded', finished_at = now()");
        var fresh = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync?background=true", token), HttpStatusCode.OK);
        Assert.Equal("idle", fresh.GetProperty("sync_state").GetString());
        Assert.Equal(1, await CountJobsAsync());

        // Last synced an hour ago: queued.
        await ExecuteAsync("UPDATE spotify_connections SET last_synced_at = now() - interval '1 hour'");
        Assert.Equal("queued", (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync?background=true", token), HttpStatusCode.Accepted))
            .GetProperty("sync_state").GetString());
        Assert.Equal(2, await CountJobsAsync());

        // Asked a moment ago (by the manual button, say): no 429 for the background call.
        await ExecuteAsync("UPDATE spotify_connections SET sync_state = 'idle', sync_requested_at = now(); UPDATE jobs SET status = 'succeeded', finished_at = now()");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync?background=true", token)).StatusCode);

        // A lapsed authorization: the status says so, without a 409.
        await ExecuteAsync("UPDATE spotify_connections SET sync_state = 'reauth_required', sync_requested_at = NULL");
        Assert.Equal("reauth_required", (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/spotify/sync?background=true", token), HttpStatusCode.OK))
            .GetProperty("sync_state").GetString());
    }

    private static async Task<string> AssertCodeRejectedAsync(HttpResponseMessage response)
    {
        var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("code", error.GetProperty("field").GetString());
        return error.GetProperty("message").GetString()!;
    }

    private async Task<Guid> SeedSignalsAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO artists (name, spotify_artist_id) VALUES ('Artist', 'sp-artist');
            INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks)
            SELECT c.user_id, a.id, 3 FROM spotify_connections c, artists a;
            SELECT user_id FROM spotify_connections;
            """);
    }

    private async Task<int> CountSignalsAsync(Guid userId)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM user_artist_signals WHERE user_id = @userId", new { userId });
    }

    private async Task<int> CountJobsAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM jobs WHERE kind = 'spotify.sync'");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(sql);
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> ConnectAsync(string token, string? redirectUri = null, string? verifier = null) =>
        SendAsync(HttpMethod.Put, "/v1/me/spotify", token,
            JsonContent.Create(new { code = "code-1", code_verifier = verifier ?? Verifier, redirect_uri = redirectUri ?? FakeSpotify.RedirectUri }));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
