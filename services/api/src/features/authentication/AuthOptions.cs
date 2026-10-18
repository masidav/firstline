namespace FandomScore.Api.Features.Authentication;

/// <summary>
/// Configuration section <c>Auth</c>. The signing key is a secret and comes from the environment
/// (<c>Auth__SigningKey</c>); the rest has safe defaults. See docs/adr/0003-authentication.md.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Base64 HMAC key for access tokens, at least 32 bytes.</summary>
    public string SigningKey { get; set; } = "";

    public string Issuer { get; set; } = "fandom-score";

    public string Audience { get; set; } = "fandom-score-api";

    /// <summary>openapi.yaml promises 15 minutes.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>An unused refresh token expires after this long, so idle devices sign in again.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Absolute session lifetime; refreshing never extends it.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(180);

    /// <summary>Sign-in and refresh calls allowed per client IP per minute.</summary>
    public int PermitsPerMinute { get; set; } = 20;

    public ProviderOptions Apple { get; set; } = new();

    public ProviderOptions Google { get; set; } = new();
}

public sealed class ProviderOptions
{
    /// <summary>
    /// Comma-separated OAuth client IDs whose identity tokens are accepted (the token's <c>aud</c>):
    /// the iOS bundle ID and Services ID for Apple; the iOS, Android, and web client IDs for Google.
    /// Empty disables the provider.
    /// </summary>
    public string ClientIds { get; set; } = "";

    public IReadOnlyList<string> ClientIdList =>
        ClientIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
