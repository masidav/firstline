namespace FandomScore.Storage;

/// <summary>Where uploaded files live. Keys are opaque; callers never build paths from user input.</summary>
public interface IFileStore
{
    Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>The file's bytes, or null when there is no such file.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Deletes the file. Deleting a file that isn't there is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Configuration section <c>Storage</c>.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>
    /// Directory for <see cref="LocalFileStore"/>. Relative paths are under the process's content root. The API and
    /// the worker must reach the same files, so give both the same directory. Local development and tests only:
    /// production needs the Cloud Storage quarantine bucket (Phase 12, not built yet).
    /// </summary>
    public string LocalPath { get; set; } = ".local-storage";
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
