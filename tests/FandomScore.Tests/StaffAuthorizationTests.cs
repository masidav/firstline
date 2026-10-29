using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>The staff policy for /admin routes, checked on a route the test maps for itself.</summary>
[Collection(PostgresCollection.Name)]
public sealed class StaffAuthorizationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string StaffPath = "/__test/staff";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync($"staff_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(_connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());

        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", _connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>());
                services.AddTransient<IStartupFilter, StaffRoute>();
            });
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Only_live_staff_pass_the_staff_policy()
    {
        var token = await SignInAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(StaffPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(token)).StatusCode);

        await SetStaffAsync(true);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(token)).StatusCode);

        // Taking staff access away works on the next request; no new token needed.
        await SetStaffAsync(false);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(token)).StatusCode);
    }

    private async Task<string> SignInAsync()
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken("g-1", "staff@example.test"), handle = "staffer" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> GetAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, StaffPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private async Task SetStaffAsync(bool isStaff)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE users SET is_staff = @isStaff", connection);
        command.Parameters.AddWithValue("isStaff", isStaff);
        await command.ExecuteNonQueryAsync();
    }

    // A branch in front of the app's pipeline with one endpoint behind the staff policy.
    private sealed class StaffRoute : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(StaffPath, branch =>
            {
                branch.UseRouting();
                branch.UseAuthentication();
                branch.UseAuthorization();
                branch.UseEndpoints(endpoints =>
                    endpoints.MapGet("/", () => Results.Ok()).RequireAuthorization(AuthSetup.StaffPolicy));
            });
            next(app);
        };
    }
}
