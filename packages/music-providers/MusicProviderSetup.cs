using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FandomScore.MusicProviders;

public static class MusicProviderSetup
{
    /// <summary>
    /// Apple Music and Tidal options and clients. Each provider stays off until configured; a malformed Apple Music
    /// key stops startup. Needs a <see cref="TimeProvider"/> registered, and the Spotify package's token protector
    /// (<c>Spotify__TokenKey</c>) for stored tokens.
    /// </summary>
    public static IServiceCollection AddMusicProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(MusicProviderOptions.Section);
        AppleMusicClient.ValidateKey((section.Get<MusicProviderOptions>() ?? new MusicProviderOptions()).AppleMusic);
        services.Configure<MusicProviderOptions>(section);
        services.AddHttpClient<AppleMusicClient>(http => http.Timeout = ProviderHttp.RequestTimeout)
            .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddHttpClient<TidalClient>(http => http.Timeout = ProviderHttp.RequestTimeout)
            .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        return services;
    }
}
