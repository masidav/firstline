namespace FandomScore.Worker.Hosting;

/// <summary>
/// Reads secrets mounted as files (one file per configuration key, e.g. <c>Spotify__TokenKey</c>) from the
/// directory named by <c>Secrets__Directory</c>. That is how Secret Manager secrets reach pods on GKE
/// (infrastructure/k8s). Locally the directory is unset and environment variables are used as before.
/// </summary>
public static class SecretsDirectory
{
    public const string Key = "Secrets:Directory";

    public static IConfigurationBuilder AddSecretsDirectory(this IConfigurationManager configuration)
    {
        var directory = configuration[Key];
        return string.IsNullOrWhiteSpace(directory)
            ? configuration
            : configuration.AddKeyPerFile(directory, optional: true, reloadOnChange: false);
    }
}
