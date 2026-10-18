using System.Security.Cryptography;
using System.Text;
using FandomScore.Spotify;

namespace FandomScore.Tests;

public sealed class SpotifyTokenProtectorTests
{
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public void Round_trips_without_storing_the_token_in_plain_text()
    {
        var protector = new SpotifyTokenProtector(FakeSpotify.Options());

        var stored = protector.Protect(User, "refresh-secret");

        Assert.DoesNotContain("refresh-secret", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
        Assert.Equal("refresh-secret", protector.Unprotect(User, stored));
        Assert.NotEqual(stored, protector.Protect(User, "refresh-secret"));
    }

    [Fact]
    public void A_token_moved_to_another_user_or_altered_does_not_decrypt()
    {
        var protector = new SpotifyTokenProtector(FakeSpotify.Options());
        var stored = protector.Protect(User, "refresh-secret");

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.NewGuid(), stored));
        var altered = (byte[])stored.Clone();
        altered[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(User, altered));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(User, [1, 2, 3]));
    }

    [Fact]
    public void Previous_keys_still_decrypt_and_dropped_keys_do_not()
    {
        var oldOptions = FakeSpotify.Options();
        var stored = new SpotifyTokenProtector(oldOptions).Protect(User, "refresh-secret");
        var newKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var rotated = new SpotifyTokenProtector(new SpotifyOptions { TokenKey = newKey, PreviousTokenKeys = oldOptions.TokenKey });
        Assert.Equal("refresh-secret", rotated.Unprotect(User, stored));

        var dropped = new SpotifyTokenProtector(new SpotifyOptions { TokenKey = newKey });
        Assert.ThrowsAny<CryptographicException>(() => dropped.Unprotect(User, stored));
    }

    [Fact]
    public void Startup_requires_a_32_byte_key_once_spotify_is_enabled()
    {
        SpotifyTokenProtector.Validate(new SpotifyOptions());
        Assert.Throws<InvalidOperationException>(() => SpotifyTokenProtector.Validate(new SpotifyOptions { ClientId = "id" }));
        Assert.Throws<InvalidOperationException>(() => SpotifyTokenProtector.Validate(
            new SpotifyOptions { ClientId = "id", TokenKey = Convert.ToBase64String(new byte[16]) }));
        Assert.Throws<InvalidOperationException>(() => SpotifyTokenProtector.Validate(
            new SpotifyOptions { ClientId = "id", TokenKey = "not base64!" }));
    }
}
