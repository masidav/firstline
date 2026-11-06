using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FandomScore.MusicProviders;

/// <summary>
/// Apple Music developer tokens for the whole process (one per <see cref="AppleMusicOptions.DeveloperTokenLifetime"/>,
/// renewed a minute before expiry), signed with the MusicKit key held here for the life of the process. A singleton
/// on purpose: the typed <see cref="AppleMusicClient"/> is transient, and the token library caches signature
/// providers per key, so a key owned and disposed by one client instance would be used again by the next. Signature
/// provider caching is also turned off for the same reason.
/// </summary>
public sealed class AppleMusicDeveloperTokens(IOptions<MusicProviderOptions> options, TimeProvider time) : IDisposable
{
    private readonly AppleMusicOptions _apple = options.Value.AppleMusic;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ECDsa? _key;
    private (string Token, DateTimeOffset ExpiresAt)? _current;

    /// <summary>An ES256 JWT: the team id as issuer, the key id in the header, good for the configured lifetime.</summary>
    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_current is { } current && current.ExpiresAt > time.GetUtcNow())
        {
            return current.Token;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_current is { } fresh && fresh.ExpiresAt > time.GetUtcNow())
            {
                return fresh.Token;
            }

            if (_key is null)
            {
                var key = ECDsa.Create();
                key.ImportFromPem(_apple.PrivateKey);
                _key = key;
            }

            var now = time.GetUtcNow().UtcDateTime;
            var expires = now + _apple.DeveloperTokenLifetime;
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = _apple.TeamId,
                IssuedAt = now,
                NotBefore = now,
                Expires = expires,
                SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_key) { KeyId = _apple.KeyId }, SecurityAlgorithms.EcdsaSha256)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
                },
            });
            _current = (token, new DateTimeOffset(expires) - TimeSpan.FromMinutes(1));
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        _key?.Dispose();
    }
}
