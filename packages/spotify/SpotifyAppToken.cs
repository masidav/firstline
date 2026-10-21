namespace FandomScore.Spotify;

/// <summary>
/// The current app access token (client credentials), shared by every <see cref="SpotifyClient"/> in the process.
/// Held in memory only. Spotify issues them for an hour; one is fetched again a minute before it expires.
/// </summary>
public sealed class SpotifyAppToken : IDisposable
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private volatile Entry? _current;

    internal async Task<string> GetAsync(
        TimeProvider time, Func<CancellationToken, Task<(string Token, TimeSpan Lifetime)>> fetch,
        CancellationToken cancellationToken)
    {
        if (_current is { } current && current.ExpiresAt > time.GetUtcNow())
        {
            return current.Token;
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (_current is { } fresh && fresh.ExpiresAt > time.GetUtcNow())
            {
                return fresh.Token;
            }

            var (token, lifetime) = await fetch(cancellationToken);
            _current = new Entry(token, time.GetUtcNow() + lifetime - TimeSpan.FromMinutes(1));
            return token;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Drops a token Spotify rejected, so the next call fetches a new one.</summary>
    internal void Invalidate(string token)
    {
        if (_current?.Token == token)
        {
            _current = null;
        }
    }

    public void Dispose() => _refresh.Dispose();

    private sealed record Entry(string Token, DateTimeOffset ExpiresAt);
}
