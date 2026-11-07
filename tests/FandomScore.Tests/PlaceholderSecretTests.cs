using FandomScore.Api.Features.Authentication;
using FandomScore.Spotify;
using FandomScore.Worker.Concerts;
using FandomScore.Worker.Notifications.Email;

namespace FandomScore.Tests;

/// <summary>
/// A Secret Manager version cannot be empty, so an unconfigured provider's secret is a single space on GKE
/// (infrastructure/README.md). The processes must treat that as "not set up".
/// </summary>
public sealed class PlaceholderSecretTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n")]
    public void Whitespace_secrets_leave_the_provider_disabled(string placeholder)
    {
        Assert.False(new SpotifyOptions { ClientId = "id", ClientSecret = placeholder }.IsCatalogEnabled);
        Assert.False(new TicketmasterOptions { ApiKey = placeholder }.IsEnabled);
        Assert.False(new SmtpOptions { Password = placeholder }.HasPassword);
        Assert.Contains("Email__Smtp__Password", new EmailOptions
        {
            Enabled = true, Provider = EmailOptions.SmtpProvider, From = "Fandom Score <d57590971@gmail.com>",
            AppOrigin = "https://app.test", Smtp = { Password = placeholder },
        }.Problem(), StringComparison.Ordinal);
        Assert.False(new AppleOptions { ClientIds = "app", TeamId = "t", KeyId = "k", PrivateKey = placeholder }.CanRevoke);
        AppleClient.ValidateKey(new AppleOptions { PrivateKey = placeholder });
    }

    [Fact]
    public void Real_secrets_enable_the_provider()
    {
        Assert.True(new SpotifyOptions { ClientId = "id", ClientSecret = "s" }.IsCatalogEnabled);
        Assert.True(new TicketmasterOptions { ApiKey = "k" }.IsEnabled);
    }
}
