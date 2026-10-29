using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Spotify;
using FandomScore.Tests.Contract;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>/me/spotify/player (now-playing bar and play buttons) against a real database, with Spotify faked.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SpotifyPlayerApiTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private static readonly FakeSpotify.TrackDetail Song =
        new("4uLU6hMCjMI75M1A2tKUQC", "Hard Times", "alb-1", "After Laughter", ("sp-paramore", "Paramore"));

    private readonly ConcurrentQueue<string> _logs = new();
    private readonly FakeSpotify _spotify = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"spotify_player_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        var options = FakeSpotify.Options();
        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("App:PublicOrigin", TestAuth.PublicOrigin);
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Spotify:ClientId", options.ClientId);
            builder.UseSetting("Spotify:RedirectUris", options.RedirectUris);
            builder.UseSetting("Spotify:TokenKey", options.TokenKey);
            builder.UseSetting("Spotify:PlayerLockWait", "00:00:00.4");
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
    public async Task Without_a_connection_or_the_playback_scopes_the_bar_says_so_and_commands_are_409()
    {
        var token = await SignInAsync("noplayer");
        Assert.Equal("not_connected", (await PlayerAsync(token)).GetProperty("status").GetString());
        await AssertProblemAsync(await SendAsync(HttpMethod.Put, "/v1/me/spotify/player/pause", token), HttpStatusCode.Conflict, "spotify_not_connected");

        // A connection made before 2026-10-26 lacks the playback scopes.
        await ConnectAsync(token);
        await ExecuteAsync("UPDATE spotify_connections SET scopes = array_remove(array_remove(scopes, 'user-read-playback-state'), 'user-modify-playback-state')");
        Assert.Equal("reconnect_needed", (await PlayerAsync(token)).GetProperty("status").GetString());
        await AssertProblemAsync(await PlayAsync(token, Song.Id), HttpStatusCode.Conflict, "spotify_reauth_required");
        Assert.DoesNotContain(_spotify.Calls, c => c.Path.StartsWith("me/player", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shows_the_song_playing_with_one_token_refresh_and_saves_a_rotated_refresh_token()
    {
        var token = await SignInAsync("listener");
        await ConnectAsync(token);
        _spotify.TokenResponse = FakeSpotify.Tokens("refresh-2");   // Spotify rotates it on refresh
        _spotify.Playback = FakeSpotify.Playing(Song);

        var player = await PlayerAsync(token);
        Assert.Equal("playing", player.GetProperty("status").GetString());
        var track = player.GetProperty("track");
        Assert.Equal(Song.Id, track.GetProperty("spotify_track_id").GetString());
        Assert.Equal("Hard Times", track.GetProperty("title").GetString());
        Assert.Equal("Paramore", track.GetProperty("artists")[0].GetProperty("name").GetString());
        Assert.Equal("After Laughter", track.GetProperty("album_title").GetString());
        Assert.Equal(200_000, track.GetProperty("duration_ms").GetInt32());
        Assert.Equal(42_000, player.GetProperty("progress_ms").GetInt32());
        Assert.Equal("Fan's iPhone", player.GetProperty("device").GetProperty("name").GetString());

        await using (var connection = await _db.OpenConnectionAsync())
        {
            var row = await connection.QuerySingleAsync<(Guid UserId, byte[] Token)>("SELECT user_id, refresh_token_enc FROM spotify_connections");
            Assert.Equal("refresh-2", new SpotifyTokenProtector(FakeSpotify.Options()).Unprotect(row.UserId, row.Token));
        }

        // A command clears the cached state; the access token is reused for it and the next read.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, "/v1/me/spotify/player/pause", token)).StatusCode);
        _spotify.Playback = FakeSpotify.Playing(Song, isPlaying: false);
        Assert.Equal("paused", (await PlayerAsync(token)).GetProperty("status").GetString());
        _spotify.Playback = null;
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/v1/me/spotify/player/next", token)).StatusCode);
        Assert.Equal("nothing_playing", (await PlayerAsync(token)).GetProperty("status").GetString());

        Assert.Single(_spotify.Calls, c => c.Path == "token" && c.Form.GetValueOrDefault("grant_type") == "refresh_token");
        Assert.All(_spotify.Calls.Where(c => c.Path.StartsWith("me/player", StringComparison.Ordinal)), c => Assert.Equal("access-1", c.Bearer));
        Assert.Contains(_spotify.Calls, c => c is { Method: "PUT", Path: "me/player/pause" });
        Assert.Contains(_spotify.Calls, c => c is { Method: "POST", Path: "me/player/next" });
        Assert.DoesNotContain(_logs, line => line.Contains("access-1", StringComparison.Ordinal) || line.Contains("refresh-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Play_starts_the_song_and_falls_back_to_the_fans_phone_but_never_a_speaker()
    {
        var token = await SignInAsync("player");
        await ConnectAsync(token);

        Assert.Equal(HttpStatusCode.NoContent, (await PlayAsync(token, Song.Id)).StatusCode);
        var play = Assert.Single(_spotify.Calls, c => c.Path == "me/player/play");
        Assert.Equal("PUT", play.Method);
        Assert.Contains($"spotify:track:{Song.Id}", play.Body, StringComparison.Ordinal);
        Assert.Equal("", play.Query);

        // Spotify is open but idle on the fan's phone: the play goes there.
        _spotify.Devices.Add(FakeSpotify.Device("speaker-1", "Kitchen", "Speaker"));
        _spotify.Devices.Add(FakeSpotify.Device("phone-1", "Fan's iPhone", "Smartphone"));
        _spotify.Interruptions.Enqueue(("me/player/play", () => FakeSpotify.PlayerError(HttpStatusCode.NotFound, "NO_ACTIVE_DEVICE")));
        Assert.Equal(HttpStatusCode.NoContent, (await PlayAsync(token, Song.Id)).StatusCode);
        Assert.Equal("?device_id=phone-1", _spotify.Calls.Last(c => c.Path == "me/player/play").Query);

        // Only a speaker: nothing to play on.
        _spotify.Devices.RemoveAt(1);
        _spotify.Interruptions.Enqueue(("me/player/play", () => FakeSpotify.PlayerError(HttpStatusCode.NotFound, "NO_ACTIVE_DEVICE")));
        await AssertProblemAsync(await PlayAsync(token, Song.Id), HttpStatusCode.Conflict, "spotify_no_device");

        // Resume sends no song.
        Assert.Equal(HttpStatusCode.NoContent, (await PlayAsync(token, null)).StatusCode);
        Assert.Equal("", _spotify.Calls.Last(c => c.Path == "me/player/play").Body);
    }

    [Fact]
    public async Task Bad_ids_free_accounts_and_revoked_access_are_reported()
    {
        var token = await SignInAsync("freefan");
        await ConnectAsync(token);
        await ExecuteAsync("UPDATE spotify_connections SET sync_state = 'idle'");   // its first sync has run

        await AssertProblemAsync(await PlayAsync(token, "not-a-track"), HttpStatusCode.UnprocessableEntity, "validation_failed");

        _spotify.Interruptions.Enqueue(("me/player/play", () => FakeSpotify.PlayerError(HttpStatusCode.Forbidden, "PREMIUM_REQUIRED")));
        await AssertProblemAsync(await PlayAsync(token, Song.Id), HttpStatusCode.Conflict, "spotify_premium_required");

        // The fan removed the app in their Spotify settings: the cached token and the refresh token stop working.
        _spotify.Interruptions.Enqueue(("me/player/pause", () => FakeSpotify.Status(HttpStatusCode.Unauthorized)));
        _spotify.TokenStatus = HttpStatusCode.BadRequest;
        _spotify.TokenResponse = new { error = "invalid_grant" };
        await AssertProblemAsync(await SendAsync(HttpMethod.Put, "/v1/me/spotify/player/pause", token), HttpStatusCode.Conflict, "spotify_reauth_required");

        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal("reauth_required", await connection.ExecuteScalarAsync<string>("SELECT sync_state FROM spotify_connections"));
        Assert.Equal("reconnect_needed", (await PlayerAsync(token)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task While_a_sync_holds_the_fans_lock_the_player_answers_503_instead_of_racing_it()
    {
        var token = await SignInAsync("syncing");
        await ConnectAsync(token);
        _spotify.Playback = FakeSpotify.Playing(Song);

        await using var sync = await _db.OpenConnectionAsync();
        var userId = await sync.ExecuteScalarAsync<Guid>("SELECT user_id FROM spotify_connections");
        Assert.True(await sync.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", new { key = $"spotify.sync:{userId}" }));

        var response = await SendAsync(HttpMethod.Get, "/v1/me/spotify/player", token);
        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "spotify_unavailable");
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.DoesNotContain(_spotify.Calls, c => c.Form.GetValueOrDefault("grant_type") == "refresh_token");

        await sync.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", new { key = $"spotify.sync:{userId}" });
        Assert.Equal("playing", (await PlayerAsync(token)).GetProperty("status").GetString());
    }

    private async Task<JsonElement> PlayerAsync(string token) =>
        await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/spotify/player", token), HttpStatusCode.OK);

    private Task<HttpResponseMessage> PlayAsync(string token, string? trackId) =>
        SendAsync(HttpMethod.Put, "/v1/me/spotify/player/play", token,
            trackId is null ? null : JsonContent.Create(new { spotify_track_id = trackId }));

    private async Task ConnectAsync(string token) =>
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/spotify", token,
            JsonContent.Create(new { code = "code-1", code_verifier = new string('v', 43), redirect_uri = FakeSpotify.RedirectUri })), HttpStatusCode.OK);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(sql);
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        Assert.Equal(code, (await JsonAsync(response, status)).GetProperty("code").GetString());

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
