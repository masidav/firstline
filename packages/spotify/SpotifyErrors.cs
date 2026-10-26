namespace FandomScore.Spotify;

/// <summary>
/// The user must connect Spotify again: the authorization code or refresh token was rejected
/// (<c>invalid_grant</c>), or Spotify returned 401 for a fresh access token.
/// Messages never include codes or tokens.
/// </summary>
public sealed class SpotifyAuthorizationException(string message) : Exception(message);

/// <summary>
/// Spotify refused a request for a reason retrying will not fix: a bad request, or 403 (in development mode,
/// a Spotify account that is not on the app's user allowlist).
/// </summary>
public sealed class SpotifyRequestException(int status, string? error)
    : Exception($"Spotify returned {status}{(error is null ? "" : $" ({error})")}.")
{
    public int Status { get; } = status;

    /// <summary>Spotify's OAuth <c>error</c> code, or a player command's <c>reason</c>, when it sent one.</summary>
    public string? Error { get; } = error;

    public bool IsForbidden => Status == 403;

    /// <summary>A player command needs Spotify Premium.</summary>
    public bool IsPremiumRequired => Status == 403 && Error == "PREMIUM_REQUIRED";

    /// <summary>A player command found no active device (Spotify isn't open anywhere, or has gone idle).</summary>
    public bool IsNoActiveDevice => Status == 404;
}

/// <summary>Spotify is down, slow, or rate limiting us. Try again later.</summary>
public sealed class SpotifyUnavailableException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
