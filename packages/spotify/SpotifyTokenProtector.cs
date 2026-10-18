using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FandomScore.Spotify;

/// <summary>
/// Encrypts Spotify refresh tokens for <c>spotify_connections.refresh_token_enc</c> with AES-256-GCM.
/// Layout: version (1 byte), key id (4 bytes), nonce (12), tag (16), ciphertext. The user id and header are
/// authenticated too, so a token copied to another user's row does not decrypt.
/// </summary>
public sealed class SpotifyTokenProtector
{
    private const byte Version = 1;
    private const int KeyIdSize = 4;
    private const int HeaderSize = 1 + KeyIdSize;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _currentKey;
    private readonly Dictionary<uint, byte[]> _keys = [];

    public SpotifyTokenProtector(IOptions<SpotifyOptions> options)
        : this(options.Value)
    {
    }

    public SpotifyTokenProtector(SpotifyOptions options)
    {
        if (options.TokenKey.Length == 0)
        {
            return;
        }

        _currentKey = ParseKey(options.TokenKey, "Spotify:TokenKey");
        _keys[KeyId(_currentKey)] = _currentKey;
        foreach (var previous in options.PreviousTokenKeyList)
        {
            var key = ParseKey(previous, "Spotify:PreviousTokenKeys");
            _keys.TryAdd(KeyId(key), key);
        }
    }

    /// <summary>Fails startup when Spotify is enabled without a usable key, instead of at the first connection.</summary>
    public static void Validate(SpotifyOptions options)
    {
        if (options.IsEnabled && options.TokenKey.Length == 0)
        {
            throw new InvalidOperationException(
                "Spotify:TokenKey is required when Spotify:ClientId is set (openssl rand -base64 32). See .env.example.");
        }

        _ = new SpotifyTokenProtector(options);
    }

    public byte[] Protect(Guid userId, string refreshToken)
    {
        var key = _currentKey ?? throw new InvalidOperationException("Spotify:TokenKey is not configured.");
        var plaintext = Encoding.UTF8.GetBytes(refreshToken);
        var output = new byte[HeaderSize + NonceSize + TagSize + plaintext.Length];
        output[0] = Version;
        BitConverter.TryWriteBytes(output.AsSpan(1, KeyIdSize), KeyId(key));
        var nonce = output.AsSpan(HeaderSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(HeaderSize + NonceSize + TagSize),
            output.AsSpan(HeaderSize + NonceSize, TagSize), AssociatedData(userId, output.AsSpan(0, HeaderSize)));
        return output;
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the data is damaged, belongs to another user, or its key is unknown.</summary>
    public string Unprotect(Guid userId, byte[] data)
    {
        if (data.Length < HeaderSize + NonceSize + TagSize || data[0] != Version)
        {
            throw new CryptographicException("Unknown Spotify token format.");
        }

        if (!_keys.TryGetValue(BitConverter.ToUInt32(data, 1), out var key))
        {
            throw new CryptographicException("The Spotify token was encrypted with a key that is no longer configured.");
        }

        var plaintext = new byte[data.Length - HeaderSize - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(data.AsSpan(HeaderSize, NonceSize), data.AsSpan(HeaderSize + NonceSize + TagSize),
            data.AsSpan(HeaderSize + NonceSize, TagSize), plaintext, AssociatedData(userId, data.AsSpan(0, HeaderSize)));
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] AssociatedData(Guid userId, ReadOnlySpan<byte> header) =>
        [.. header, .. Encoding.UTF8.GetBytes($"spotify-refresh-token:{userId:D}")];

    // Not secret: identifies which configured key encrypted a token.
    private static uint KeyId(byte[] key) => BitConverter.ToUInt32(SHA256.HashData(key), 0);

    private static byte[] ParseKey(string value, string name)
    {
        var bytes = new byte[value.Length];
        if (!Convert.TryFromBase64String(value, bytes, out var length) || length != 32)
        {
            throw new InvalidOperationException($"{name} must be base64 for exactly 32 random bytes (openssl rand -base64 32).");
        }

        return bytes[..length];
    }
}
