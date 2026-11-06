using FandomScore.Tests.Contract;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.MusicProviders;
using FandomScore.Spotify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>/me/music against a real database, with Apple Music and Tidal faked (migration 0058).</summary>
[Collection(PostgresCollection.Name)]
public sealed class MusicApiTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private static readonly string Verifier = new('v', 43);

    private readonly ConcurrentQueue<string> _logs = new();
    private readonly FakeAppleMusic _apple = new();
    private readonly FakeTidal _tidal = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"music_api_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("App:PublicOrigin", TestAuth.PublicOrigin);
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Spotify:ClientId", FakeSpotify.ClientId);
            builder.UseSetting("Spotify:TokenKey", FakeSpotify.TokenKey);
            builder.UseSetting("MusicProviders:AppleMusic:TeamId", FakeAppleMusic.TeamId);
            builder.UseSetting("MusicProviders:AppleMusic:KeyId", FakeAppleMusic.KeyId);
            builder.UseSetting("MusicProviders:AppleMusic:PrivateKey", FakeAppleMusic.PrivateKey);
            builder.UseSetting("MusicProviders:Tidal:ClientId", FakeTidal.ClientId);
            builder.UseSetting("MusicProviders:Tidal:RedirectUris", FakeTidal.RedirectUri);
            builder.UseSetting("Logging:LogLevel:Default", "Debug");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_logs)));
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>());
                services.AddHttpClient<AppleMusicClient>().ConfigurePrimaryHttpMessageHandler(() => _apple);
                services.AddHttpClient<TidalClient>().ConfigurePrimaryHttpMessageHandler(() => _tidal);
            });
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    public void Dispose()
    {
        _apple.Dispose();
        _tidal.Dispose();
    }

    [Fact]
    public async Task Without_connections_both_providers_say_not_connected()
    {
        var token = await SignInAsync("fan_one");

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/music", token), HttpStatusCode.OK);
        Assert.Equal(["apple_music", "tidal"], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("provider").GetString()));
        Assert.All(list.GetProperty("items").EnumerateArray(), i => Assert.False(i.GetProperty("connected").GetBoolean()));

        var problem = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/music/deezer", token), HttpStatusCode.NotFound);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());

        // Me carries the same two statuses, so the app needs no second call.
        var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", token), HttpStatusCode.OK);
        Assert.Equal(["apple_music", "tidal"], me.GetProperty("music").EnumerateArray().Select(i => i.GetProperty("provider").GetString()));
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/music/apple_music", token, new { music_user_token = "mut-1" }), HttpStatusCode.OK);
        me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", token), HttpStatusCode.OK);
        Assert.True(me.GetProperty("music")[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Apple_music_connects_with_a_checked_user_token_stored_encrypted_and_queues_a_sync()
    {
        var token = await SignInAsync("fan_one");

        var developer = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/music/apple_music/developer-token", token), HttpStatusCode.OK);
        Assert.StartsWith("eyJ", developer.GetProperty("token").GetString(), StringComparison.Ordinal);
        Assert.Equal(12 * 3600, developer.GetProperty("expires_in").GetInt32());

        var status = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/music/apple_music", token, new { music_user_token = "mut-1" }), HttpStatusCode.OK);
        Assert.Equal(("apple_music", true, "queued"), (status.GetProperty("provider").GetString(), status.GetProperty("connected").GetBoolean(), status.GetProperty("sync_state").GetString()));
        Assert.Equal("me/storefront", Assert.Single(_apple.Calls).Path);

        await using var connection = await _db.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<(Guid UserId, byte[] Token, string? Storefront)>(
            "SELECT user_id, token_enc, storefront FROM music_connections WHERE provider = 'apple_music'");
        Assert.Equal("mut-1", new SpotifyTokenProtector(FakeSpotify.Options()).Unprotect(row.UserId, row.Token, MusicProvider.TokenPurpose("apple_music")));
        Assert.Equal("us", row.Storefront);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM jobs WHERE kind = 'music.sync' AND payload->>'provider' = 'apple_music'"));
        Assert.DoesNotContain(_logs, line => line.Contains("mut-1", StringComparison.Ordinal));

        // A token Apple rejects is a validation error, and nothing is stored.
        var refused = await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/music/apple_music", token, new { music_user_token = "mut-bad" }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("music_user_token", refused.GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Equal("mut-1", new SpotifyTokenProtector(FakeSpotify.Options()).Unprotect(row.UserId,
            (await connection.ExecuteScalarAsync<byte[]>("SELECT token_enc FROM music_connections WHERE provider = 'apple_music'"))!, MusicProvider.TokenPurpose("apple_music")));
    }

    [Fact]
    public async Task Tidal_connects_by_code_exchange_one_account_per_fan_and_disconnecting_deletes_only_its_rows()
    {
        var one = await SignInAsync("fan_one");
        var two = await SignInAsync("fan_two");

        var status = await JsonAsync(await ConnectTidalAsync(one), HttpStatusCode.OK);
        Assert.Equal(("tidal", true, "queued"), (status.GetProperty("provider").GetString(), status.GetProperty("connected").GetBoolean(), status.GetProperty("sync_state").GetString()));
        var exchange = Assert.Single(_tidal.Calls, c => c.Path == "token").Form;
        Assert.Equal(("authorization_code", "code-1", Verifier, FakeTidal.RedirectUri, FakeTidal.ClientId),
            (exchange["grant_type"], exchange["code"], exchange["code_verifier"], exchange["redirect_uri"], exchange["client_id"]));
        Assert.False(exchange.ContainsKey("client_secret"));

        // The same Tidal account can't qualify a second fan account; the history row is kept for the reuse rule.
        var inUse = await JsonAsync(await ConnectTidalAsync(two), HttpStatusCode.Conflict);
        Assert.Equal("music_in_use", inUse.GetProperty("code").GetString());

        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM spotify_account_links"));
        var userId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM users WHERE handle = 'fan_one'");
        await connection.ExecuteAsync(
            """
            INSERT INTO artists (name, spotify_artist_id, status, activated_at, catalog_loaded_at) VALUES ('X', 'sp-x', 'onboarding', now(), now());
            INSERT INTO user_artist_signals (user_id, provider, artist_id, liked_tracks) SELECT @userId, 'tidal', id, 2 FROM artists;
            INSERT INTO user_artist_signals (user_id, provider, artist_id, liked_tracks) SELECT @userId, 'spotify', id, 9 FROM artists;
            """,
            new { userId });

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/v1/me/music/tidal", one)).StatusCode);
        Assert.Equal(["spotify"], await connection.QueryAsync<string>("SELECT provider FROM user_artist_signals WHERE user_id = @userId", new { userId }));
        Assert.False((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/music/tidal", one), HttpStatusCode.OK)).GetProperty("connected").GetBoolean());
        // Now the second fan can connect it.
        await JsonAsync(await ConnectTidalAsync(two), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bad_tidal_requests_are_rejected_before_calling_tidal_and_a_missing_scope_is_a_validation_error()
    {
        var token = await SignInAsync("fan_one");

        var problem = await JsonAsync(await ConnectTidalAsync(token, redirectUri: "https://evil.example/callback", verifier: "short"), HttpStatusCode.UnprocessableEntity);
        Assert.Equal(["code_verifier", "redirect_uri"], problem.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()));
        Assert.Empty(_tidal.Calls);

        _tidal.TokenResponse = FakeTidal.Tokens("r", scope: "user.read");
        var scopes = await JsonAsync(await ConnectTidalAsync(token), HttpStatusCode.UnprocessableEntity);
        Assert.Contains("collection.read", scopes.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);

        _tidal.TokenStatus = HttpStatusCode.BadRequest;
        _tidal.TokenResponse = new { error = "invalid_grant" };
        var expired = await JsonAsync(await ConnectTidalAsync(token), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("code", expired.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Manual_sync_is_rate_limited_and_needs_a_working_connection()
    {
        var token = await SignInAsync("fan_one");
        var missing = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/music/apple_music/sync", token), HttpStatusCode.Conflict);
        Assert.Equal("music_not_connected", missing.GetProperty("code").GetString());

        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/music/apple_music", token, new { music_user_token = "mut-1" }), HttpStatusCode.OK);
        // Queued by the connect: returned as is.
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/music/apple_music/sync", token), HttpStatusCode.Accepted);

        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE music_connections SET sync_state = 'idle', last_synced_at = now()");
        var limited = await SendAsync(HttpMethod.Post, "/v1/me/music/apple_music/sync", token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        await connection.ExecuteAsync("UPDATE music_connections SET sync_requested_at = now() - interval '1 hour'");
        Assert.Equal("queued", (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/music/apple_music/sync", token), HttpStatusCode.Accepted)).GetProperty("sync_state").GetString());

        await connection.ExecuteAsync("UPDATE music_connections SET sync_state = 'reauth_required'");
        Assert.Equal("music_reauth_required", (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/me/music/apple_music/sync", token), HttpStatusCode.Conflict)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Without_configuration_a_provider_is_unavailable()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MusicProviders:Tidal:ClientId", "");
            builder.UseSetting("MusicProviders:AppleMusic:TeamId", "");
        });
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken("g-off", "off@example.test"), handle = "fan_off" });
        var token = (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;

        foreach (var (path, body) in new (string, object)[] { ("/v1/me/music/tidal", new { code = "c", code_verifier = Verifier, redirect_uri = FakeTidal.RedirectUri }), ("/v1/me/music/apple_music", new { music_user_token = "mut-1" }) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var problem = await JsonAsync(await client.SendAsync(request), HttpStatusCode.ServiceUnavailable);
            Assert.Equal("music_unavailable", problem.GetProperty("code").GetString());
        }
    }

    private Task<HttpResponseMessage> ConnectTidalAsync(string token, string? redirectUri = null, string? verifier = null) =>
        SendAsync(HttpMethod.Put, "/v1/me/music/tidal", token, new { code = "code-1", code_verifier = verifier ?? Verifier, redirect_uri = redirectUri ?? FakeTidal.RedirectUri });

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
