namespace FandomScore.Storage;

/// <summary>
/// Where uploaded files live. Keys are opaque; callers never build paths from user input. Keys start with the
/// storage tier: <c>quarantine/</c> (unredacted uploads), <c>archive/</c> (redacted proof), or <c>media/</c>
/// (staff-published media such as featured videos). A storage failure throws <see cref="IOException"/>, so callers
/// can leave the work queued and retry.
/// </summary>
public interface IFileStore
{
    Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>The file's bytes, or null when there is no such file.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Deletes the file. Deleting a file that isn't there is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// A link that downloads the file directly from storage until <paramref name="lifetime"/> passes, or null when this
    /// store can't make one (then the API serves the bytes itself).
    /// </summary>
    Task<Uri?> SignedReadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) => Task.FromResult<Uri?>(null);
}

/// <summary>Configuration section <c>Storage</c>.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary><c>local</c> (disk, the default) or <c>gcs</c> (Cloud Storage buckets).</summary>
    public string Provider { get; set; } = "local";

    /// <summary>
    /// Directory for <see cref="LocalFileStore"/>. Relative paths are under the process's content root. The API and
    /// the worker must reach the same files, so give both the same directory. Local development and tests only.
    /// </summary>
    public string LocalPath { get; set; } = ".local-storage";

    /// <summary>Cloud Storage bucket for <c>quarantine/</c> keys (unredacted uploads; the bucket deletes them at 30 days).</summary>
    public string QuarantineBucket { get; set; } = "";

    /// <summary>Cloud Storage bucket for <c>archive/</c> keys (redacted proof only).</summary>
    public string ArchiveBucket { get; set; } = "";

    /// <summary>Cloud Storage bucket for <c>media/</c> keys (staff-published media, kept until the app deletes it).</summary>
    public string MediaBucket { get; set; } = "";

    /// <summary>
    /// A Cloud Storage emulator's base address (e.g. <c>http://localhost:4443</c>) for tests and local runs: requests go
    /// there without credentials, and signed links are made with a throwaway key the emulator doesn't check.
    /// </summary>
    public string EmulatorHost { get; set; } = "";
}

/// <summary>Files on local disk, one file per key (keys are server-made GUID paths).</summary>
public sealed class LocalFileStore(string root) : IFileStore
{
    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content.ToArray(), cancellationToken);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        // File.Delete ignores a missing file but throws when its directory is missing; neither is an error here.
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        // Keys are made by the server ("quarantine/<guid>"), but refuse anything that could leave the root.
        if (key.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(key))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        return Path.Combine(root, key);
    }
}
