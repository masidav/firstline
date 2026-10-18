using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FandomScore.Spotify;

public static class SpotifySetup
{
    /// <summary>
    /// Spotify options, client, and token encryption. Spotify stays off while <c>Spotify:ClientId</c> is empty;
    /// once set, a missing or malformed token key stops startup. Needs a <see cref="TimeProvider"/> registered.
    /// </summary>
    public static IServiceCollection AddSpotify(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SpotifyOptions.Section);
        SpotifyTokenProtector.Validate(section.Get<SpotifyOptions>() ?? new SpotifyOptions());

        services.Configure<SpotifyOptions>(section);
        services.AddSingleton<SpotifyTokenProtector>();
        // The sync job is a singleton holding its client, so connections are recycled instead of handlers
        // (picks up DNS changes).
        services.AddHttpClient<SpotifyClient>(http => http.Timeout = SpotifyClient.RequestTimeout)
            .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        return services;
    }
}
