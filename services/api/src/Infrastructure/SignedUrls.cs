using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FandomScore.Api.Features.Authentication;
using Microsoft.Extensions.Options;

namespace FandomScore.Api.Infrastructure;

/// <summary>
/// Short-lived links that need no sign-in, like a storage provider's presigned URLs: <c>?exp=&amp;sig=</c>, an HMAC of
/// the purpose, id, and expiry. Used for uploading a file and for showing one (an image tag can't send a bearer
/// token). The key is derived from <c>Auth:SigningKey</c>, so rotating that key also ends these links.
/// </summary>
public sealed class SignedUrls(IOptions<AuthOptions> auth, TimeProvider time)
{
    private readonly byte[] _key = HMACSHA256.HashData(Convert.FromBase64String(auth.Value.SigningKey), "signed-urls/v1"u8);

    /// <summary>Query string (with leading '?') for <paramref name="purpose"/> on <paramref name="id"/>, valid for <paramref name="lifetime"/>.</summary>
    public (string Query, DateTimeOffset ExpiresAt) Sign(string purpose, Guid id, TimeSpan lifetime)
    {
        var expiresAt = time.GetUtcNow().Add(lifetime);
        var exp = expiresAt.ToUnixTimeSeconds();
        return ($"?exp={exp.ToString(CultureInfo.InvariantCulture)}&sig={Signature(purpose, id, exp)}", expiresAt);
    }

    public bool Verify(string purpose, Guid id, long? exp, string? sig)
    {
        if (exp is not { } expiry || string.IsNullOrEmpty(sig) || expiry < time.GetUtcNow().ToUnixTimeSeconds())
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Signature(purpose, id, expiry)), Encoding.ASCII.GetBytes(sig));
    }

    /// <summary>The API's public origin for absolute links, from the request (forwarded headers are applied first).</summary>
    public static string Origin(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    private string Signature(string purpose, Guid id, long exp)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(FormattableString.Invariant($"{purpose}\n{id}\n{exp}")));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
