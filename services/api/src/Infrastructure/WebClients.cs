namespace FandomScore.Api.Infrastructure;

/// <summary>
/// Browser apps (the Expo web build in apps/mobile) call the API from another origin, so the API answers CORS
/// requests from the origins in <c>Cors:AllowedOrigins</c> (comma-separated, e.g. <c>http://localhost:8081</c>).
/// Empty means no browser origin is allowed. Tokens travel in the Authorization header, never in cookies,
/// so credentials stay off.
/// </summary>
public static class WebClients
{
    public static IServiceCollection AddWebClients(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = (configuration["Cors:AllowedOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders("Authorization", "Content-Type", "Idempotency-Key")
            .WithExposedHeaders("Retry-After")
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));
    }
}
