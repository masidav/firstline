using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace FandomScore.Api.Infrastructure;

/// <summary>
/// Trusts <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only from the load balancer in front of the API,
/// so per-client rate limits (sign-in) see the real client address. Off unless configured.
/// </summary>
/// <remarks>
/// Google's external Application Load Balancer appends <c>&lt;client-ip&gt;, &lt;load-balancer-ip&gt;</c> to the header
/// and connects from the Google Front End ranges, so a request is read as: connection from a known network
/// (<c>ForwardedHeaders__KnownNetworks</c>, CIDRs), the rightmost entry is the load balancer's own address
/// (<c>ForwardedHeaders__KnownProxies</c>), and the entry before it is the client. Anything a client puts in the
/// header itself sits further left and is never used. See infrastructure/README.md.
/// </remarks>
public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddLoadBalancerForwarding(this IServiceCollection services, IConfiguration configuration)
    {
        var proxies = List(configuration["ForwardedHeaders:KnownProxies"]);
        var networks = List(configuration["ForwardedHeaders:KnownNetworks"]);
        if (proxies.Length == 0 && networks.Length == 0)
        {
            return services;
        }

        return services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Two hops: the load balancer's own address, then the client it saw.
            options.ForwardLimit = 2;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
    }

    private static string[] List(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
