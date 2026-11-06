namespace FandomScore.MusicProviders;

/// <summary>
/// The fan must connect the service again: the token was rejected (401, or <c>invalid_grant</c> on a refresh).
/// Messages never include tokens or codes.
/// </summary>
public sealed class MusicAuthorizationException(string message) : Exception(message);

/// <summary>The provider refused a request for a reason retrying will not fix (a bad request, or 403).</summary>
public sealed class MusicRequestException(int status, string? error)
    : Exception($"The provider returned {status}{(error is null ? "" : $" ({error})")}.")
{
    public int Status { get; } = status;

    public string? Error { get; } = error;

    public bool IsForbidden => Status == 403;
}

/// <summary>The provider is down, slow, or rate limiting us. Try again later.</summary>
public sealed class MusicUnavailableException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
