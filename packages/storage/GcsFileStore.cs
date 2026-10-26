using System.Net;
using Google;
using Google.Cloud.Storage.V1;

namespace FandomScore.Storage;

/// <summary>
/// Files in Cloud Storage (ADR 0002). The key's first segment picks the bucket (<c>quarantine/</c>, <c>archive/</c>,
/// <c>media/</c>) and the rest is the object name, so the schema's tier rules (archive rows have <c>archive/</c>
/// keys) and the buckets' access rules line up. Content types come from the files themselves: every stored file is
/// a JPEG, PNG, or MP4.
/// </summary>
public sealed class GcsFileStore(StorageClient client, UrlSigner? signer, StorageOptions options, Uri? emulator = null) : IFileStore
{
    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var (bucket, name) = Locate(key);
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        await Guard(() => client.UploadObjectAsync(bucket, name, ContentType(content.Span), stream, cancellationToken: cancellationToken));
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var (bucket, name) = Locate(key);
        using var stream = new MemoryStream();
        try
        {
            await client.DownloadObjectAsync(bucket, name, stream, cancellationToken: cancellationToken);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception e) when (e is GoogleApiException or HttpRequestException)
        {
            throw new IOException($"Cloud Storage read failed for {key}.", e);
        }

        return stream.ToArray();
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var (bucket, name) = Locate(key);
        try
        {
            await client.DeleteObjectAsync(bucket, name, cancellationToken: cancellationToken);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
        catch (Exception e) when (e is GoogleApiException or HttpRequestException)
        {
            throw new IOException($"Cloud Storage delete failed for {key}.", e);
        }
    }

    public async Task<Uri?> SignedReadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (signer is null)
        {
            return null;
        }

        var (bucket, name) = Locate(key);
        var signing = UrlSigner.Options.FromDuration(lifetime);
        if (emulator is not null)
        {
            signing = signing.WithScheme(emulator.Scheme).WithHost(emulator.Host).WithPort(emulator.Port)
                .WithUrlStyle(UrlSigner.UrlStyle.PathStyle);
        }

        var request = UrlSigner.RequestTemplate.FromBucket(bucket).WithObjectName(name).WithHttpMethod(HttpMethod.Get);
        try
        {
            return new Uri(await signer.SignAsync(request, signing, cancellationToken));
        }
        catch (Exception e) when (e is GoogleApiException or HttpRequestException or InvalidOperationException)
        {
            throw new IOException($"Signing a Cloud Storage link failed for {key}.", e);
        }
    }

    private (string Bucket, string Name) Locate(string key)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        var name = slash < 0 ? "" : key[(slash + 1)..];
        if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        var bucket = key[..slash] switch
        {
            "quarantine" => options.QuarantineBucket,
            "archive" => options.ArchiveBucket,
            "media" => options.MediaBucket,
            _ => throw new ArgumentException("Storage keys start with quarantine/, archive/, or media/.", nameof(key)),
        };
        return (bucket, name);
    }

    /// <summary>The stored file's type from its first bytes.</summary>
    public static string ContentType(ReadOnlySpan<byte> content) => content switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
        _ when content.Length > 12 && content.Slice(4, 4).SequenceEqual("ftyp"u8) => "video/mp4",
        _ => "application/octet-stream",
    };

    private static async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is GoogleApiException or HttpRequestException)
        {
            throw new IOException("Cloud Storage write failed.", e);
        }
    }
}
