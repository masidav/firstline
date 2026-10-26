using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FandomScore.Storage;

public static class StorageSetup
{
    /// <summary>
    /// Registers <see cref="IFileStore"/> from the <c>Storage</c> section. <c>Provider=local</c> (default): disk under
    /// <see cref="StorageOptions.LocalPath"/>, resolved against <paramref name="contentRoot"/> when relative.
    /// <c>Provider=gcs</c>: the three Cloud Storage buckets, with the pod's own identity (Workload Identity on GKE; no
    /// key files) or an emulator (<see cref="StorageOptions.EmulatorHost"/>).
    /// </summary>
    public static IServiceCollection AddFileStore(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var options = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        switch (options.Provider)
        {
            case "local":
                var root = Path.IsPathRooted(options.LocalPath) ? options.LocalPath : Path.Combine(contentRoot, options.LocalPath);
                services.AddSingleton<IFileStore>(new LocalFileStore(root));
                break;
            case "gcs":
                Validate(options);
                services.AddSingleton<IFileStore>(_ => CreateGcs(options));
                break;
            default:
                throw new InvalidOperationException($"Storage:Provider must be local or gcs, not '{options.Provider}'.");
        }

        return services;
    }

    private static void Validate(StorageOptions options)
    {
        var missing = new[] { (nameof(options.QuarantineBucket), options.QuarantineBucket), (nameof(options.ArchiveBucket), options.ArchiveBucket),
                              (nameof(options.MediaBucket), options.MediaBucket) }
            .Where(b => string.IsNullOrWhiteSpace(b.Item2)).Select(b => $"Storage:{b.Item1}").ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Storage:Provider is gcs but {string.Join(", ", missing)} is not set.");
        }
    }

    private static GcsFileStore CreateGcs(StorageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.EmulatorHost))
        {
            var emulator = new Uri(options.EmulatorHost.TrimEnd('/') + "/");
            var client = new StorageClientBuilder
            {
                BaseUri = new Uri(emulator, "storage/v1/").ToString(),
                UnauthenticatedAccess = true,
            }.Build();
            // The emulator doesn't check signatures; a throwaway HMAC key makes links of the real shape.
            var signer = UrlSigner.FromBlobSigner(UrlSigner.HmacBlobSigner.Create("GOOGEMULATOR", Convert.ToBase64String(Guid.NewGuid().ToByteArray())));
            return new GcsFileStore(client, signer, options, emulator);
        }

        // Application Default Credentials: on GKE the Workload Identity service account, which signs links through the
        // IAM API (it needs roles/iam.serviceAccountTokenCreator on itself; Terraform grants it). Credentials that
        // can't sign (a developer's own gcloud login) leave links to the API.
        var credential = GoogleCredential.GetApplicationDefault();
        UrlSigner? urlSigner;
        try
        {
            urlSigner = UrlSigner.FromCredential(credential);
        }
        catch (InvalidOperationException)
        {
            urlSigner = null;
        }

        return new GcsFileStore(StorageClient.Create(credential), urlSigner, options);
    }
}
