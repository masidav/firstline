using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FandomScore.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace FandomScore.Tests;

/// <summary>Local developer sign-in (Development only) and CORS for the web app.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DevSignInTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string WebOrigin = "http://localhost:8081";

    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync($"dev_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(_connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Developer_sign_in_works_in_development()
    {
        await using var factory = Factory("Development", devSignIn: true);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = "dev:alice", handle = "alice" });
        var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("alice@example.test", session.GetProperty("user").GetProperty("email").GetString());

        using var me = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(me)).StatusCode);

        var invalid = await client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = "dev:not valid!", handle = "bob" });
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task Developer_sign_in_is_off_unless_switched_on()
    {
        await using var factory = Factory("Development", devSignIn: false);

        var response = await factory.CreateClient().PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = "dev:alice", handle = "alice" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Developer_sign_in_stops_startup_outside_development(string environment)
    {
        await using var factory = Factory(environment, devSignIn: true);

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("only allowed in Development", error.Message);
    }

    [Fact]
    public async Task Allowed_web_origins_pass_cors_preflight()
    {
        await using var factory = Factory("Development", devSignIn: false);
        var client = factory.CreateClient();

        var allowed = await client.SendAsync(Preflight(WebOrigin));
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(WebOrigin, allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var other = await client.SendAsync(Preflight("https://evil.example"));
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/v1/auth/sessions");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return request;
    }

    private WebApplicationFactory<Program> Factory(string environment, bool devSignIn) =>
        new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Database", _connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Auth:DevSignIn", devSignIn ? "true" : "false");
            builder.UseSetting("Cors:AllowedOrigins", WebOrigin);
        });
}
