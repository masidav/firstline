using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// Absolute links come from App:PublicOrigin, never the Host header a client sends; Host itself is not filtered,
/// so Kubernetes probes and load-balancer health checks (which send the pod address) still get answers.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PublicOriginTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"public-origin-tests-{Guid.NewGuid():N}");
    private string _connectionString = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync($"public_origin_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(_connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(_connectionString);
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, recursive: true);
        }
    }

    [Fact]
    public async Task Links_use_the_configured_origin_whatever_host_the_client_sends()
    {
        await using var factory = Factory("Production", TestAuth.PublicOrigin + "/");
        var client = factory.CreateClient();
        var token = await SignInAsync(client, "ana");
        await ScoreAsync("ana");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/me/scores/{_artistId}/card");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Host = "evil.example";
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        request.Headers.Add("X-Forwarded-Proto", "http");
        var card = await JsonAsync(await client.SendAsync(request), HttpStatusCode.OK);

        var url = card.GetProperty("image_url").GetString()!;
        Assert.StartsWith($"{TestAuth.PublicOrigin}/v1/score-cards/", url, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", url, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(url).PathAndQuery)).StatusCode);
    }

    [Theory]
    [InlineData("10.52.3.17:8080")] // kubelet probe: the pod address
    [InlineData("10.128.0.5")] // load balancer health check
    [InlineData("api.beta.firstline.com")]
    public async Task Health_answers_for_any_host(string host)
    {
        await using var factory = Factory("Production", TestAuth.PublicOrigin);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        request.Headers.Host = host;

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Outside_development_the_api_wont_start_without_a_public_origin(string environment)
    {
        await using var factory = Factory(environment, publicOrigin: null);

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("App:PublicOrigin", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api.example.com")]
    [InlineData("ftp://api.example.com")]
    [InlineData("https://api.example.com/?x=1")]
    [InlineData("https://user:pass@api.example.com")]
    public async Task A_malformed_public_origin_stops_startup(string origin)
    {
        await using var factory = Factory("Development", origin);

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("absolute http(s) URL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_development_links_fall_back_to_the_request()
    {
        await using var factory = Factory("Development", publicOrigin: null);
        var client = factory.CreateClient();
        var token = await SignInAsync(client, "ben");
        await ScoreAsync("ben");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/me/scores/{_artistId}/card");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Host = "192.168.1.20:5271";
        var card = await JsonAsync(await client.SendAsync(request), HttpStatusCode.OK);

        Assert.StartsWith("http://192.168.1.20:5271/v1/score-cards/", card.GetProperty("image_url").GetString(), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> Factory(string environment, string? publicOrigin) =>
        new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Database", _connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Storage:LocalPath", _storage);
            if (publicOrigin is not null)
            {
                builder.UseSetting("App:PublicOrigin", publicOrigin);
            }

            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });

    private async Task ScoreAsync(string handle)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
            SELECT id, @artist, 70, 90, false, 0.4 FROM users WHERE handle = @handle
            """, connection);
        command.Parameters.AddWithValue("artist", _artistId);
        command.Parameters.AddWithValue("handle", handle);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<string> SignInAsync(HttpClient client, string handle)
    {
        var response = await client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
