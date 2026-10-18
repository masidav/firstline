using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FandomScore.Api.Features.Authentication;

/// <summary>The provider account an identity token proves.</summary>
public sealed record ProviderIdentity(string Subject, string? Email, bool EmailVerified);

/// <summary>Checks Apple and Google identity tokens sent by the app (openapi createSession).</summary>
public interface IIdentityTokenValidator
{
    bool IsEnabled(string provider);

    /// <summary>Returns null when the token is not a valid, current token for this app.</summary>
    Task<ProviderIdentity?> ValidateAsync(string provider, string idToken, string? nonce, CancellationToken cancellationToken);
}

/// <summary>One OpenID Connect provider: where its signing keys live and which issuers and audiences to accept.</summary>
public sealed class OidcProvider(
    IReadOnlyList<string> issuers,
    IReadOnlyList<string> audiences,
    IConfigurationManager<OpenIdConnectConfiguration> configuration)
{
    public IReadOnlyList<string> Issuers { get; } = issuers;
    public IReadOnlyList<string> Audiences { get; } = audiences;
    public IConfigurationManager<OpenIdConnectConfiguration> Configuration { get; } = configuration;

    public static OidcProvider Discover(string metadataAddress, IReadOnlyList<string> issuers, IReadOnlyList<string> audiences) =>
        new(issuers, audiences, new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true }));
}

public sealed class OidcIdentityTokenValidator(IReadOnlyDictionary<string, OidcProvider> providers) : IIdentityTokenValidator
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Apple and Google, enabled by the client IDs in <see cref="AuthOptions"/>.</summary>
    public static OidcIdentityTokenValidator FromOptions(AuthOptions options)
    {
        var providers = new Dictionary<string, OidcProvider>();
        if (options.Apple.ClientIdList.Count > 0)
        {
            providers["apple"] = OidcProvider.Discover(
                "https://appleid.apple.com/.well-known/openid-configuration",
                ["https://appleid.apple.com"], options.Apple.ClientIdList);
        }

        if (options.Google.ClientIdList.Count > 0)
        {
            providers["google"] = OidcProvider.Discover(
                "https://accounts.google.com/.well-known/openid-configuration",
                ["https://accounts.google.com", "accounts.google.com"], options.Google.ClientIdList);
        }

        return new OidcIdentityTokenValidator(providers);
    }

    public bool IsEnabled(string provider) => providers.ContainsKey(provider);

    public async Task<ProviderIdentity?> ValidateAsync(
        string provider, string idToken, string? nonce, CancellationToken cancellationToken)
    {
        if (!providers.TryGetValue(provider, out var oidc))
        {
            return null;
        }

        var result = await ValidateSignatureAsync(oidc, idToken, cancellationToken);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // The provider rotated its keys since we cached them.
            oidc.Configuration.RequestRefresh();
            result = await ValidateSignatureAsync(oidc, idToken, cancellationToken);
        }

        if (!result.IsValid
            || Claim(result, "sub") is not string subject || subject.Length == 0
            || !NonceMatches(Claim(result, "nonce") as string, nonce))
        {
            return null;
        }

        return new ProviderIdentity(
            subject,
            Claim(result, "email") as string,
            IsTrue(Claim(result, "email_verified")));
    }

    private static object? Claim(TokenValidationResult result, string name) =>
        result.Claims.TryGetValue(name, out var value) ? value : null;

    private static async Task<TokenValidationResult> ValidateSignatureAsync(
        OidcProvider oidc, string idToken, CancellationToken cancellationToken)
    {
        var configuration = await oidc.Configuration.GetConfigurationAsync(cancellationToken);
        return await Handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuers = oidc.Issuers,
            ValidAudiences = oidc.Audiences,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });
    }

    // Google echoes the nonce the app sent; Apple echoes what the app sent to Apple, which is its SHA-256 (hex).
    private static bool NonceMatches(string? tokenNonce, string? requestNonce)
    {
        if (requestNonce is null)
        {
            return true;
        }

        if (tokenNonce is null)
        {
            return false;
        }

        var hashed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(requestNonce)));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tokenNonce), Encoding.UTF8.GetBytes(requestNonce))
            || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tokenNonce), Encoding.UTF8.GetBytes(hashed));
    }

    // Google sends a boolean; Apple has sent both a boolean and the string "true".
    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var parsed) && parsed,
        _ => false,
    };
}
