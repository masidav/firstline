using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Tests.Contract;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SkiaSharp;

namespace FandomScore.Tests;

/// <summary>Profile pictures (migration 0052): upload, set, show to everyone, change, remove, and what deletion does.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AvatarApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"avatar-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"avatar_api_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Storage:LocalPath", _storage);
            builder.UseSetting("App:PublicOrigin", TestAuth.PublicOrigin);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();
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
    public async Task A_fan_sets_a_picture_everyone_sees_then_changes_and_removes_it()
    {
        var fan = await SignInAsync("ana");
        var other = await SignInAsync("ben");

        // A wide PNG with a tEXt chunk: stored as a square JPEG of its middle, with nothing but pixels.
        var first = await UploadAsync(fan, Png(64, 48, new SKColor(120, 80, 200)), "image/png");
        var me = await JsonAsync(await PatchMeAsync(fan, new { avatar_upload_id = first }), HttpStatusCode.OK);
        Assert.Equal($"{TestAuth.PublicOrigin}/v1/avatars/{first}", me.GetProperty("avatar_url").GetString());
        var url = Local(me.GetProperty("avatar_url").GetString()!);

        // The link needs no sign-in (image tags), and the picture is square, small, and a JPEG.
        var picture = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/jpeg", picture.Content.Headers.ContentType?.MediaType);
        using (var bitmap = SKBitmap.Decode(await picture.Content.ReadAsByteArrayAsync()))
        {
            Assert.NotNull(bitmap);
            Assert.Equal((48, 48), (bitmap.Width, bitmap.Height));
        }

        Assert.Equal("image/jpeg", await ScalarAsync<string>("SELECT mime_type FROM uploads WHERE id = @id", new { id = Guid.Parse(first) }));
        Assert.Equal($"media/avatars/{first}", await ScalarAsync<string>("SELECT storage_key FROM uploads WHERE id = @id", new { id = Guid.Parse(first) }));

        // Other fans see it wherever the fan is named.
        var page = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users/ana", other), HttpStatusCode.OK);
        Assert.Equal(me.GetProperty("avatar_url").GetString(), page.GetProperty("avatar_url").GetString());
        var people = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/users?q=ana", other), HttpStatusCode.OK);
        Assert.Equal(me.GetProperty("avatar_url").GetString(), people.GetProperty("items")[0].GetProperty("avatar_url").GetString());

        // A new picture: the old link stops at once and the old file is queued for storage.cleanup.
        var second = await UploadAsync(fan, Jpeg(1200, 800, new SKColor(20, 160, 90)));
        me = await JsonAsync(await PatchMeAsync(fan, new { avatar_upload_id = second }), HttpStatusCode.OK);
        Assert.Equal($"{TestAuth.PublicOrigin}/v1/avatars/{second}", me.GetProperty("avatar_url").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(url)).StatusCode);
        Assert.False(await ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM uploads WHERE id = @id)", new { id = Guid.Parse(first) }));
        Assert.True(await ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM storage_deletions WHERE storage_key = @key)", new { key = $"media/avatars/{first}" }));
        using (var bitmap = SKBitmap.Decode(await _client.GetByteArrayAsync(Local(me.GetProperty("avatar_url").GetString()!))))
        {
            Assert.Equal((512, 512), (bitmap!.Width, bitmap.Height));   // big photos are scaled down
        }

        // Sending the current picture again changes nothing.
        me = await JsonAsync(await PatchMeAsync(fan, new { avatar_upload_id = second }), HttpStatusCode.OK);
        Assert.Equal($"{TestAuth.PublicOrigin}/v1/avatars/{second}", me.GetProperty("avatar_url").GetString());

        // Removing it.
        me = await JsonAsync(await PatchMeAsync(fan, new { avatar_upload_id = (string?)null }), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("avatar_url").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/v1/avatars/{second}")).StatusCode);
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM uploads WHERE user_id = (SELECT id FROM users WHERE handle = 'ana')"));
    }

    [Fact]
    public async Task A_picture_must_be_the_fans_own_finished_avatar_upload()
    {
        var fan = await SignInAsync("ana");
        var other = await SignInAsync("ben");

        async Task RefusedAsync(object? uploadId)
        {
            var problem = await ProblemAsync(await PatchMeAsync(fan, new { avatar_upload_id = uploadId }), 422);
            Assert.Equal("avatar_upload_id", problem.GetProperty("errors")[0].GetProperty("field").GetString());
        }

        await RefusedAsync(Guid.NewGuid());                                               // no such upload
        await RefusedAsync("not-a-uuid");
        await RefusedAsync(await UploadAsync(fan, Jpeg(64, 64, SKColors.Red), purpose: "submission"));   // a proof photo
        await RefusedAsync(await UploadAsync(other, Jpeg(64, 64, SKColors.Red)));           // someone else's picture
        var unfinished = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", fan,
            new { purpose = "avatar", mime_type = "image/jpeg", bytes = 100 }), HttpStatusCode.Created);
        await RefusedAsync(unfinished.GetProperty("upload_id").GetString());              // slot without its file

        // Bytes that aren't a picture are refused at upload time, and a video can't be a picture.
        var slot = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", fan,
            new { purpose = "avatar", mime_type = "image/jpeg", bytes = 10 }), HttpStatusCode.Created);
        var garbage = await PutAsync(slot.GetProperty("url").GetString()!, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, garbage.StatusCode);
        var video = await ProblemAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", fan,
            new { purpose = "avatar", mime_type = "video/mp4", bytes = 10 }), 422);
        Assert.Equal("mime_type", video.GetProperty("errors")[0].GetProperty("field").GetString());

        Assert.Equal(JsonValueKind.Null, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", fan), HttpStatusCode.OK)).GetProperty("avatar_url").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/v1/avatars/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_account_takes_the_picture_down()
    {
        var fan = await SignInAsync("ana");
        var upload = await UploadAsync(fan, Jpeg(64, 64, SKColors.Blue));
        var me = await JsonAsync(await PatchMeAsync(fan, new { avatar_upload_id = upload }), HttpStatusCode.OK);
        var url = Local(me.GetProperty("avatar_url").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(url)).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Delete, "/v1/me", fan)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(url)).StatusCode);
        Assert.True(await ScalarAsync<bool>(
            "SELECT avatar_upload_id IS NULL AND avatar_url IS NULL FROM users WHERE id = (SELECT user_id FROM uploads WHERE id = @id)",
            new { id = Guid.Parse(upload) }));
    }

    /// <summary>A real JPEG of the given size (a photo, as the app sends it).</summary>
    private static byte[] Jpeg(int width, int height, SKColor color) => Encode(width, height, color, SKEncodedImageFormat.Jpeg);

    private static byte[] Png(int width, int height, SKColor color) => Encode(width, height, color, SKEncodedImageFormat.Png);

    private static byte[] Encode(int width, int height, SKColor color, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private async Task<string> UploadAsync(string token, byte[] photo, string mimeType = "image/jpeg", string purpose = "avatar")
    {
        var intent = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", token,
            new { purpose, mime_type = mimeType, bytes = photo.Length }), HttpStatusCode.Created);
        var response = await PutAsync(intent.GetProperty("url").GetString()!, photo, mimeType);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return intent.GetProperty("upload_id").GetString()!;
    }

    private Task<HttpResponseMessage> PutAsync(string url, byte[] content, string mimeType = "image/jpeg")
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        return _client.PutAsync(Local(url), body);
    }

    /// <summary>Links are built on the public origin; the test server is addressed by path.</summary>
    private static string Local(string url) => url.Replace(TestAuth.PublicOrigin, "", StringComparison.Ordinal);

    private Task<HttpResponseMessage> PatchMeAsync(string token, object body) => SendAsync(HttpMethod.Patch, "/v1/me", token, body);

    private async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
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
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"Expected {expected}, got {response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, int status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"Expected {status}, got {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }
}
