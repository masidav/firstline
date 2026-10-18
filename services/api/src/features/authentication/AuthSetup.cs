using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FandomScore.Api.Features.Authentication;

public static class AuthSetup
{
    public const string SignInRateLimit = "sign-in";

    /// <summary>
    /// Bearer access tokens, checked against a live session on every request. Program.cs requires them on
    /// the whole /v1 group (openapi's global <c>security</c>); endpoints opt out with <c>AllowAnonymous</c>.
    /// Fails at startup when the signing key is missing or weak.
    /// </summary>
    public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AuthOptions.Section);
        var options = section.Get<AuthOptions>() ?? new AuthOptions();
        if (!IsStrongKey(options.SigningKey))
        {
            throw new InvalidOperationException(
                "Auth:SigningKey must be base64 for at least 32 random bytes (openssl rand -base64 32). See .env.example.");
        }

        services.Configure<AuthOptions>(section);
        services.AddSingleton<SessionService>();
        services.AddSingleton<IIdentityTokenValidator>(sp =>
            OidcIdentityTokenValidator.FromOptions(sp.GetRequiredService<IOptions<AuthOptions>>().Value));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = options.Issuer,
                ValidAudience = options.Audience,
                IssuerSigningKey = SessionService.SigningKey(options),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
            };
            jwt.Events = new JwtBearerEvents { OnTokenValidated = RequireActiveSessionAsync };
        });
        services.AddAuthorization();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
            // Per client IP. Behind the GKE load balancer this needs forwarded headers (Phase 12).
            limiter.AddPolicy(SignInRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.PermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        return services;
    }

    public static Guid UserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

    public static Guid SessionId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sid)!);

    private static async Task RequireActiveSessionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)
            || !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sid), out var sessionId))
        {
            context.Fail("The access token does not name a user and session.");
            return;
        }

        var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
        if (!await sessions.IsActiveAsync(userId, sessionId, context.HttpContext.RequestAborted))
        {
            context.Fail("The session has ended.");
        }
    }

    private static bool IsStrongKey(string key)
    {
        var bytes = new byte[key.Length];
        return Convert.TryFromBase64String(key, bytes, out var length) && length >= 32;
    }
}
