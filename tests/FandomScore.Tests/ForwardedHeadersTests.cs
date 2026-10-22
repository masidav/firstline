using System.Net;
using System.Net.Http.Json;
using FandomScore.Api.Features.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FandomScore.Tests;

/// <summary>Behind the load balancer, the per-client sign-in limit keys on the forwarded client address.</summary>
public sealed class ForwardedHeadersTests : IDisposable
{
    private const string LoadBalancerIp = "203.0.113.10";

    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Database", "Host=localhost;Database=unused");
        builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
        builder.UseSetting("Auth:PermitsPerMinute", "1");
        builder.UseSetting("ForwardedHeaders:KnownProxies", LoadBalancerIp);
        builder.UseSetting("ForwardedHeaders:KnownNetworks", "130.211.0.0/22, 35.191.0.0/16");
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>());
            // The test server has no socket; pretend every connection comes from a Google Front End.
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(IPAddress.Parse("130.211.1.1")));
        });
    });

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Sign_in_limit_follows_the_forwarded_client_address()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(client, $"198.51.100.1, {LoadBalancerIp}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(client, $"198.51.100.1, {LoadBalancerIp}"));
        // A different client behind the same load balancer has its own allowance.
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(client, $"198.51.100.2, {LoadBalancerIp}"));
    }

    [Fact]
    public async Task Client_supplied_entries_further_left_are_ignored()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(client, $"10.0.0.1, 198.51.100.3, {LoadBalancerIp}"));
        // Changing the spoofable part does not reset the limit for the real client.
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(client, $"10.0.0.2, 198.51.100.3, {LoadBalancerIp}"));
    }

    private static async Task<HttpStatusCode> SignInAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/sessions")
        {
            Content = JsonContent.Create(new { provider = "google", id_token = "forged" }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private sealed class RemoteAddressFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, pipeline) =>
            {
                context.Connection.RemoteIpAddress = address;
                return pipeline(context);
            });
            next(app);
        };
    }
}
