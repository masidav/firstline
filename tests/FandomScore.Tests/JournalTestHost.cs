using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// An API host over a fresh database seeded with one active artist and three albums (one delisted), for the
/// journal, comments, and lists tests. Spotify is off (no client id), so nothing here can call it.
/// </summary>
public sealed class JournalTestHost : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _logs = new();
    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    public NpgsqlDataSource Db { get; private set; } = null!;

    public Guid ArtistId { get; private set; }

    public Guid InRainbows { get; private set; }

    public Guid TheBends { get; private set; }

    public Guid LostEp { get; private set; }

    public static async Task<JournalTestHost> StartAsync(PostgresFixture postgres, string name)
    {
        var host = new JournalTestHost();
        var connectionString = await postgres.CreateDatabaseAsync($"{name}_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        host.Db = NpgsqlDataSource.Create(connectionString);
        await host.SeedAsync();

        host._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(host._logs)));
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        host.Client = host._factory.CreateClient();
        return host;
    }

    private async Task SeedAsync()
    {
        await using var connection = await Db.OpenConnectionAsync();
        // Separate statements: a data-modifying CTE can't see rows another CTE of the same statement inserted.
        ArtistId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO artists (name, spotify_artist_id, image_url, catalog_loaded_at) VALUES ('Radiohead', 'sp-radiohead', 'https://i.scdn.co/rh', now()) RETURNING id");
        await connection.ExecuteAsync(
            """
            INSERT INTO artist_baselines (artist_id, dist_params, curve) VALUES (@ArtistId, '{}', '[]');
            UPDATE artists SET status = 'active' WHERE id = @ArtistId;
            """,
            new { ArtistId });
        InRainbows = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO albums (artist_id, title, release_date, release_date_precision, spotify_album_id, cover_url, album_type)
            VALUES (@ArtistId, 'In Rainbows', '2007-10-10', 'day', 'al-rainbows', 'https://i.scdn.co/al-rainbows', 'album') RETURNING id
            """,
            new { ArtistId });
        TheBends = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO albums (artist_id, title, release_date, release_date_precision, spotify_album_id, cover_url, album_type)
            VALUES (@ArtistId, 'The Bends', '1995-01-01', 'year', 'al-bends', 'https://i.scdn.co/al-bends', 'album') RETURNING id
            """,
            new { ArtistId });
        LostEp = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO albums (artist_id, title, release_date, spotify_album_id, cover_url, album_type, delisted_at)
            VALUES (@ArtistId, 'Lost EP', '2001-05-05', 'al-lost', 'https://i.scdn.co/al-lost', 'single', now()) RETURNING id
            """,
            new { ArtistId });
    }

    public async Task<string> SignInAsync(string handle)
    {
        var response = await Client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    public Task<HttpResponseMessage> GetAsync(string path, string token) => SendAsync(HttpMethod.Get, path, token);

    public Task<HttpResponseMessage> PostAsync(string path, string token, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Client.SendAsync(request);
    }

    /// <summary>Creates a log of <paramref name="albumId"/> and returns its id.</summary>
    public async Task<string> LogAsync(string token, Guid albumId, string visibility = "public")
    {
        var result = await JsonAsync(await PostAsync("/v1/logs", token, new { album_id = albumId, visibility }), HttpStatusCode.Created);
        return result.GetProperty("log").GetProperty("id").GetString()!;
    }

    public async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        var errors = string.Join("\n", _logs.Where(l => l.Contains("Exception", StringComparison.Ordinal)).TakeLast(3));
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}\n{errors}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Db.DisposeAsync();
    }
}
