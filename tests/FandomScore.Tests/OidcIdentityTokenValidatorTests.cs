using System.Security.Cryptography;
using System.Text;
using FandomScore.Api.Features.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FandomScore.Tests;

/// <summary>The real Apple/Google token checks, with a local signing key instead of the provider's.</summary>
public sealed class OidcIdentityTokenValidatorTests : IDisposable
{
    private const string Issuer = "https://issuer.test";
    private const string ClientId = "app-client";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _otherRsa = RSA.Create(2048);
    private readonly OidcIdentityTokenValidator _validator;

    public OidcIdentityTokenValidatorTests()
    {
        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(new RsaSecurityKey(_rsa) { KeyId = "k1" });
        _validator = new OidcIdentityTokenValidator(new Dictionary<string, OidcProvider>
        {
            ["google"] = new([Issuer], [ClientId], new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration)),
        });
    }

    public void Dispose()
    {
        _rsa.Dispose();
        _otherRsa.Dispose();
    }

    [Fact]
    public async Task Valid_token_returns_the_provider_identity()
    {
        var identity = await _validator.ValidateAsync("google", Token(), null, CancellationToken.None);

        Assert.Equal(new ProviderIdentity("user-123", "fan@example.test", true), identity);
    }

    [Fact]
    public async Task String_email_verified_is_understood()
    {
        var identity = await _validator.ValidateAsync(
            "google", Token(claims: new() { ["email_verified"] = "true" }), null, CancellationToken.None);
        var unverified = await _validator.ValidateAsync(
            "google", Token(claims: new() { ["email_verified"] = "false" }), null, CancellationToken.None);

        Assert.True(identity!.EmailVerified);
        Assert.False(unverified!.EmailVerified);
    }

    [Theory]
    [InlineData("wrong audience")]
    [InlineData("wrong issuer")]
    [InlineData("expired")]
    [InlineData("other key")]
    [InlineData("no subject")]
    public async Task Invalid_tokens_are_rejected(string problem)
    {
        var token = problem switch
        {
            "wrong audience" => Token(descriptor => descriptor.Audience = "someone-else"),
            "wrong issuer" => Token(descriptor => descriptor.Issuer = "https://evil.test"),
            "expired" => Token(descriptor =>
            {
                descriptor.IssuedAt = descriptor.NotBefore = DateTime.UtcNow.AddHours(-2);
                descriptor.Expires = DateTime.UtcNow.AddHours(-1);
            }),
            "other key" => Token(descriptor => descriptor.SigningCredentials =
                new SigningCredentials(new RsaSecurityKey(_otherRsa) { KeyId = "k2" }, SecurityAlgorithms.RsaSha256)),
            "no subject" => Token(claims: new() { ["sub"] = "" }),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        Assert.Null(await _validator.ValidateAsync("google", token, null, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_provider_is_rejected()
    {
        Assert.False(_validator.IsEnabled("apple"));
        Assert.Null(await _validator.ValidateAsync("apple", Token(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Nonce_must_match_raw_or_sha256()
    {
        const string nonce = "n-0S6_WzA2Mj";
        var hashed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));

        Assert.NotNull(await _validator.ValidateAsync("google", Token(claims: new() { ["nonce"] = nonce }), nonce, CancellationToken.None));
        Assert.NotNull(await _validator.ValidateAsync("google", Token(claims: new() { ["nonce"] = hashed }), nonce, CancellationToken.None));
        Assert.Null(await _validator.ValidateAsync("google", Token(claims: new() { ["nonce"] = "other" }), nonce, CancellationToken.None));
        Assert.Null(await _validator.ValidateAsync("google", Token(), nonce, CancellationToken.None));
        Assert.Null(await _validator.ValidateAsync("google", Token(claims: new() { ["nonce"] = nonce }), "", CancellationToken.None));
    }

    private string Token(Action<SecurityTokenDescriptor>? change = null, Dictionary<string, object>? claims = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "user-123",
                ["email"] = "fan@example.test",
                ["email_verified"] = true,
            },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        };
        foreach (var (name, value) in claims ?? [])
        {
            descriptor.Claims[name] = value;
        }

        change?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
