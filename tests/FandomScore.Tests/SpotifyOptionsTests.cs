using FandomScore.Spotify;
using Microsoft.Extensions.Configuration;

namespace FandomScore.Tests;

public class SpotifyOptionsTests
{
    [Fact]
    public void Client_secret_is_trimmed_when_bound()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Spotify:ClientSecret"] = "abc123\n" })
            .Build();

        var options = configuration.GetSection("Spotify").Get<SpotifyOptions>()!;

        Assert.Equal("abc123", options.ClientSecret);
    }

    [Fact]
    public void A_single_space_still_means_unset()
    {
        var options = new SpotifyOptions { ClientId = "id", TokenKey = Convert.ToBase64String(new byte[32]), ClientSecret = " " };

        Assert.False(options.IsCatalogEnabled);
    }
}
