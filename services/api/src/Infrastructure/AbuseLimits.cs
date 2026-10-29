using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace FandomScore.Api.Infrastructure;

/// <summary>
/// Request limits from the security pass (2026-10-28). Everything answers the usual 429 with <c>Retry-After</c>
/// (AuthSetup). Per signed-in fan where there is one, else per client IP (forwarded headers apply behind the load
/// balancer).
/// <list type="bullet">
/// <item>A global ceiling on every request (<see cref="GlobalPerMinute"/>); the health probe is exempt.</item>
/// <item>Named policies for calls that cost Spotify quota, background work, or image rendering.</item>
/// </list>
/// </summary>
public static class AbuseLimits
{
    /// <summary>Adding grab-bag songs: a Spotify lookup each, and possibly a new artist to onboard.</summary>
    public const string GrabBagAdds = "grab-bag-adds";

    /// <summary>Asking for a rescore of an artist (queues an artist-wide job).</summary>
    public const string Rescores = "rescores";

    /// <summary>Score cards: the signed link (per fan) and the drawn image (per IP; the link needs no sign-in).</summary>
    public const string ScoreCards = "score-cards";

    /// <summary>Counting link clicks, follows, and unfollows: cheap, but easy to script.</summary>
    public const string Churn = "churn";

    public const int GlobalPerMinute = 1200;

    /// <summary>The body size every request may send; uploads raise it for themselves (UploadEndpoints).</summary>
    public const long MaxBodyBytes = 1024 * 1024;

    public static IServiceCollection AddAbuseLimits(this IServiceCollection services) =>
        services.Configure<RateLimiterOptions>(limiter =>
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                http.Request.Path.StartsWithSegments("/v1/health")
                    ? RateLimitPartition.GetNoLimiter("health")
                    : Window(Caller(http), GlobalPerMinute, TimeSpan.FromMinutes(1)));
            limiter.AddPolicy(GrabBagAdds, http => Window(Caller(http), 30, TimeSpan.FromHours(1)));
            limiter.AddPolicy(Rescores, http => Window(Caller(http), 10, TimeSpan.FromHours(1)));
            limiter.AddPolicy(ScoreCards, http => Window(Caller(http), 60, TimeSpan.FromMinutes(1)));
            limiter.AddPolicy(Churn, http => Window(Caller(http), 120, TimeSpan.FromMinutes(1)));
        });

    /// <summary>The signed-in fan, else the client IP.</summary>
    private static string Caller(HttpContext http) =>
        http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) is { } user
            ? $"user:{user}"
            : $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    private static RateLimitPartition<string> Window(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
        });
}
