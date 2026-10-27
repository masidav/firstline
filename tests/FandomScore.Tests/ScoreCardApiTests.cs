using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Score cards: a signed link to a PNG of the caller's fandom score for an artist.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ScoreCardApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"score-card-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"score_card_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Storage:LocalPath", _storage);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, recursive: true);
        }
    }

    [Fact]
    public async Task A_fan_gets_a_signed_link_to_their_card_and_the_png_shows_their_standing()
    {
        var ana = await SignInAsync("ana");
        await ScoreAsync("ana", 96.6m, estimated: true);

        var card = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/me/scores/{_artistId}/card", ana), HttpStatusCode.OK);
        Assert.Equal(("Top 3%", true, "I'm in the top 3% of Geese fans on Fandom Score."), (card.GetProperty("top_label").GetString(),
            card.GetProperty("is_estimated").GetBoolean(), card.GetProperty("share_text").GetString()));
        var url = new Uri(card.GetProperty("image_url").GetString()!);

        // No sign-in needed for the image: the link is the key.
        var image = await _client.GetAsync(url.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        var png = await image.Content.ReadAsByteArrayAsync();
        using (var bitmap = SkiaSharp.SKBitmap.Decode(png))
        {
            Assert.Equal((1080, 1080), (bitmap.Width, bitmap.Height));
        }

        if (Environment.GetEnvironmentVariable("SCORE_CARD_PREVIEW") is { Length: > 0 } preview)
        {
            await File.WriteAllBytesAsync(preview, png);
        }

        // Tampered or for another artist: refused.
        var tampered = url.PathAndQuery.Replace("sig=", "sig=x", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(tampered)).StatusCode);
        var otherArtist = url.PathAndQuery.Replace(_artistId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(otherArtist)).StatusCode);
    }

    [Fact]
    public async Task No_score_or_a_paused_artist_means_no_card()
    {
        var ben = await SignInAsync("ben");
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/me/scores/{_artistId}/card", ben), HttpStatusCode.NotFound);
        await ScoreAsync("ben", 40m, estimated: false);
        var card = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/me/scores/{_artistId}/card", ben), HttpStatusCode.OK);
        Assert.Equal("Top 60%", card.GetProperty("top_label").GetString());
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE artists SET status = 'paused' WHERE id = @_artistId", new { _artistId });
        }

        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/me/scores/{_artistId}/card", ben), HttpStatusCode.NotFound);
        var url = new Uri(card.GetProperty("image_url").GetString()!);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(url.PathAndQuery)).StatusCode);
    }

    [Fact]
    public void Labels_round_like_the_app_and_long_names_still_render()
    {
        Assert.Equal("Top 1%", FandomScore.Api.Features.Sharing.ScoreCard.TopLabel(99.9m));
        Assert.Equal("Top 1%", FandomScore.Api.Features.Sharing.ScoreCard.TopLabel(100m));
        Assert.Equal("Top 50%", FandomScore.Api.Features.Sharing.ScoreCard.TopLabel(50.4m));
        var png = FandomScore.Api.Features.Sharing.ScoreCard.Render(new FandomScore.Api.Features.Sharing.ScoreCardContent(
            "King Gizzard & The Lizard Wizard and Their Extremely Long Touring Name", 97m, false, "a_fan_with_a_long_handle", new DateOnly(2026, 10, 27)));
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal(1080, bitmap.Width);
        if (Environment.GetEnvironmentVariable("SCORE_CARD_PREVIEW_LONG") is { Length: > 0 } preview)
        {
            File.WriteAllBytes(preview, png);
        }
    }

    private async Task ScoreAsync(string handle, decimal percentile, bool estimated)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
            SELECT id, @_artistId, 70, @percentile, @estimated, 0.4 FROM users WHERE handle = @handle
            """,
            new { _artistId, percentile, estimated, handle });
    }

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private async Task<string> StaffAsync(string handle)
    {
        var token = await SignInAsync(handle);
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = @handle", new { handle });
        return token;
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }
}
