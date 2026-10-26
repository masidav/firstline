using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FandomScore.Api.Features.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FandomScore.Tests;

public sealed class RateLimitTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
    {
        // Rejected identity tokens never reach the database.
        builder.UseSetting("ConnectionStrings:Database", "Host=localhost;Database=unused");
        builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
        builder.UseSetting("Auth:PermitsPerMinute", "2");
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
    });

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Sign_in_attempts_are_limited_per_client()
    {
        var client = _factory.CreateClient();
        var attempt = new { provider = "google", id_token = "forged" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/v1/auth/sessions", attempt)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/v1/auth/sessions", attempt)).StatusCode);
        var limited = await client.PostAsJsonAsync("/v1/auth/sessions", attempt);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var problem = JsonDocument.Parse(await limited.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
    }
}
