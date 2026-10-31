using Microsoft.Extensions.Options;

namespace FandomScore.Api.Infrastructure;

/// <summary>
/// Configuration section <c>App</c>. <c>App:PublicOrigin</c> (e.g. <c>https://api.beta.firstline-5730kl.com</c>) is the
/// API's public address; every absolute link the API hands out (signed file links, score cards, featured videos)
/// starts with it, never with the request's Host header, which a client can set to anything.
/// </summary>
/// <remarks>
/// The API deliberately keeps <c>AllowedHosts=*</c>: Kubernetes probes and the load balancer's health checks
/// send the pod address as Host, and host filtering would fail them. Outside Development the API refuses to start
/// without a public origin (<see cref="PublicOriginGuard"/>); in Development links fall back to the request.
/// </remarks>
public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>Scheme, host, and optional port (and path base), without a trailing slash. Empty: use the request.</summary>
    public string PublicOrigin { get; set; } = "";
}

public static class PublicOrigin
{
    public static IServiceCollection AddPublicOrigin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AppOptions>().Bind(configuration.GetSection(AppOptions.Section)).ValidateOnStart();
        return services.AddSingleton<IValidateOptions<AppOptions>, PublicOriginGuard>();
    }

    /// <summary>The configured public origin, or (Development only, when unset) the request's own.</summary>
    public static string For(HttpRequest request)
    {
        var configured = request.HttpContext.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value.PublicOrigin;
        return string.IsNullOrWhiteSpace(configured)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}"
            : configured.Trim().TrimEnd('/');
    }

    /// <summary>An absolute http(s) URL with no query, fragment, or credentials.</summary>
    public static bool IsValid(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}

/// <summary>Fails startup outside Development when <c>App:PublicOrigin</c> is missing, and anywhere when it is malformed.</summary>
public sealed class PublicOriginGuard(IHostEnvironment environment) : IValidateOptions<AppOptions>
{
    public ValidateOptionsResult Validate(string? name, AppOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PublicOrigin))
        {
            return environment.IsDevelopment()
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    $"App:PublicOrigin (the API's public URL, e.g. https://api.example.com) must be set in {environment.EnvironmentName}; " +
                    "links are never built from the request's Host header there. See .env.example.");
        }

        return PublicOrigin.IsValid(options.PublicOrigin)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("App:PublicOrigin must be an absolute http(s) URL such as https://api.example.com.");
    }
}
