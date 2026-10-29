using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FandomScore.Api.Features.Authentication;

/// <summary>
/// Calls Apple's Sign in with Apple REST API. Used to revoke the user's Apple sign-in for this app when they
/// delete their account. We keep no Apple tokens: the app sends a fresh authorization code with the deletion,
/// which is exchanged for a refresh token and revoked straight away.
/// </summary>
public sealed partial class AppleClient(
    HttpClient http, IOptions<AuthOptions> options, IIdentityTokenValidator validator, TimeProvider time,
    ILogger<AppleClient> logger)
{
    public static readonly Uri BaseAddress = new("https://appleid.apple.com/");

    private readonly AppleOptions _apple = options.Value.Apple;

    /// <summary>
    /// Best effort: deletion has already happened, so failures are logged (without codes or tokens), not thrown.
    /// </summary>
    public async Task RevokeAsync(string? authorizationCode, string appleSubject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(authorizationCode))
        {
            LogNoAuthorizationCode(logger);
            return;
        }

        if (!_apple.CanRevoke)
        {
            LogNotConfigured(logger);
            return;
        }

        // Native iOS authorization codes are issued to the bundle ID, listed first (see .env.example).
        var clientId = _apple.ClientIdList[0];
        var clientSecret = ClientSecret(clientId);

        using var tokenResponse = await http.PostAsync(new Uri("auth/token", UriKind.Relative), new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = authorizationCode,
                ["grant_type"] = "authorization_code",
            }), cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            LogAppleError(logger, "token", (int)tokenResponse.StatusCode);
            return;
        }

        var tokens = await tokenResponse.Content.ReadFromJsonAsync<AppleTokens>(cancellationToken);
        // No nonce: this token comes straight from Apple's token endpoint (our own TLS call, client secret), not from a
        // client, so there is nothing to replay; the subject check below ties it to the account being deleted.
        var identity = tokens?.IdToken is { } idToken
            ? await validator.ValidateAsync("apple", idToken, nonce: null, cancellationToken)
            : null;
        if (tokens?.RefreshToken is not { } refreshToken || identity?.Subject != appleSubject)
        {
            // The code belongs to a different Apple account, or the response was incomplete.
            LogWrongAccount(logger);
            return;
        }

        using var revokeResponse = await http.PostAsync(new Uri("auth/revoke", UriKind.Relative), new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["token"] = refreshToken,
                ["token_type_hint"] = "refresh_token",
            }), cancellationToken);
        if (!revokeResponse.IsSuccessStatusCode)
        {
            LogAppleError(logger, "revoke", (int)revokeResponse.StatusCode);
        }
    }

    /// <summary>Apple's client secret: a short-lived ES256 JWT signed with the Sign in with Apple key.</summary>
    private string ClientSecret(string clientId)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(_apple.PrivateKey);
        var now = time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _apple.TeamId,
            Audience = "https://appleid.apple.com",
            Subject = new System.Security.Claims.ClaimsIdentity([new("sub", clientId)]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key) { KeyId = _apple.KeyId }, SecurityAlgorithms.EcdsaSha256),
        });
    }

    /// <summary>Fails startup on a malformed key instead of at the first account deletion.</summary>
    public static void ValidateKey(AppleOptions apple)
    {
        // Whitespace is the "not set up" placeholder (a Secret Manager version cannot be empty).
        if (string.IsNullOrWhiteSpace(apple.PrivateKey))
        {
            return;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(apple.PrivateKey);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException("Auth:Apple:PrivateKey is not a PEM EC private key (.p8).", e);
        }
        catch (CryptographicException e)
        {
            throw new InvalidOperationException("Auth:Apple:PrivateKey is not a PEM EC private key (.p8).", e);
        }
    }

    public sealed record AppleTokens(
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleted an Apple account without an authorization code; the Apple sign-in was not revoked.")]
    private static partial void LogNoAuthorizationCode(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Apple revocation is not configured (Auth:Apple TeamId, KeyId, PrivateKey); the Apple sign-in was not revoked.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Apple {Endpoint} call failed with status {Status}; the Apple sign-in was not revoked.")]
    private static partial void LogAppleError(ILogger logger, string endpoint, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Apple authorization code is for a different Apple account or incomplete; the Apple sign-in was not revoked.")]
    private static partial void LogWrongAccount(ILogger logger);
}
