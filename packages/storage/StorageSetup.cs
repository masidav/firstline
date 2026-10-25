using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FandomScore.Storage;

public static class StorageSetup
{
    /// <summary>
    /// Registers <see cref="IFileStore"/> from the <c>Storage</c> section: local disk under
    /// <see cref="StorageOptions.LocalPath"/>, resolved against <paramref name="contentRoot"/> when relative.
    /// </summary>
    public static IServiceCollection AddFileStore(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var options = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        var root = Path.IsPathRooted(options.LocalPath) ? options.LocalPath : Path.Combine(contentRoot, options.LocalPath);
        services.AddSingleton<IFileStore>(new LocalFileStore(root));
        return services;
    }
}
