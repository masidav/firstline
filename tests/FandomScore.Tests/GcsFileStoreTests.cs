using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Storage;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// Cloud Storage behind <see cref="IFileStore"/> (packages/storage), against a Cloud Storage emulator
/// (fake-gcs-server) in Docker: keys route to the quarantine, archive, and media buckets; files keep their content
/// type; missing files read as null; failures are <see cref="IOException"/>; signed links download the file; and the
/// API's file links redirect to storage instead of streaming the bytes.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GcsFileStoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly StorageOptions Buckets = new()
    {
        Provider = "gcs", QuarantineBucket = "test-quarantine", ArchiveBucket = "test-archive", MediaBucket = "test-media",
    };

    private IContainer _emulator = null!;
    private string _emulatorHost = "";
    private StorageClient _client = null!;
    private IFileStore _store = null!;

    public async Task InitializeAsync()
    {
        // The emulator writes its own address into download links, so it must know the host port up front.
        var port = FreePort();
        _emulatorHost = $"http://127.0.0.1:{port}";
        _emulator = new ContainerBuilder("fsouza/fake-gcs-server:1.52")
            .WithPortBinding(port, 4443)
            .WithCommand("-scheme", "http", "-port", "4443", "-backend", "memory",
                "-external-url", _emulatorHost, "-public-host", $"127.0.0.1:{port}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(4443).ForPath("/storage/v1/b")))
            .Build();
        await _emulator.StartAsync();

        _client = new StorageClientBuilder { BaseUri = $"{_emulatorHost}/storage/v1/", UnauthenticatedAccess = true }.Build();
        foreach (var bucket in new[] { Buckets.QuarantineBucket, Buckets.ArchiveBucket, Buckets.MediaBucket })
        {
            await _client.CreateBucketAsync("test-project", bucket);
        }

        _store = StoreFor(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build());
    }

    public async Task DisposeAsync() => await _emulator.DisposeAsync();

    [Fact]
    public async Task Each_tier_goes_to_its_own_bucket()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 };
        byte[] mp4 = [0, 0, 0, 24, .. "ftypisom"u8, 0, 0, 2, 0];

        await _store.PutAsync("quarantine/a1", jpeg, CancellationToken.None);
        await _store.PutAsync("archive/a1.png", png, CancellationToken.None);
        await _store.PutAsync("media/v1", mp4, CancellationToken.None);

        Assert.Equal(jpeg.Length, (int)(await _client.GetObjectAsync("test-quarantine", "a1")).Size!);
        Assert.Equal(png.Length, (int)(await _client.GetObjectAsync("test-archive", "a1.png")).Size!);
        Assert.Equal(mp4.Length, (int)(await _client.GetObjectAsync("test-media", "v1")).Size!);
        Assert.Equal(jpeg, await _store.ReadAsync("quarantine/a1", CancellationToken.None));
        Assert.Equal(png, await _store.ReadAsync("archive/a1.png", CancellationToken.None));
        Assert.Equal(mp4, await _store.ReadAsync("media/v1", CancellationToken.None));
    }

    // The emulator drops content types on upload (real Cloud Storage keeps them), so the type the store sends is
    // checked here directly.
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10 }, "image/png")]
    [InlineData(new byte[] { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0 }, "video/mp4")]
    [InlineData(new byte[] { 1, 2, 3 }, "application/octet-stream")]
    public void Files_are_stored_with_the_content_type_their_bytes_show(byte[] content, string expected)
    {
        Assert.Equal(expected, GcsFileStore.ContentType(content));
    }

    [Fact]
    public async Task Missing_files_read_as_null_and_deleting_twice_is_fine()
    {
        await _store.PutAsync("quarantine/gone", new byte[] { 0xFF, 0xD8, 0xFF }, CancellationToken.None);
        await _store.DeleteAsync("quarantine/gone", CancellationToken.None);
        await _store.DeleteAsync("quarantine/gone", CancellationToken.None);

        Assert.Null(await _store.ReadAsync("quarantine/gone", CancellationToken.None));
        Assert.Null(await _store.ReadAsync("archive/never-there.jpg", CancellationToken.None));
    }

    [Theory]
    [InlineData("elsewhere/x")]
    [InlineData("quarantine/")]
    [InlineData("quarantine")]
    [InlineData("quarantine/../archive/x")]
    public async Task Keys_outside_the_three_tiers_are_refused(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.PutAsync(key, new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task A_storage_failure_is_an_IOException_so_callers_can_retry()
    {
        var broken = StoreFor(new ConfigurationBuilder().AddInMemoryCollection(Settings(quarantine: "no-such-bucket")).Build());

        await Assert.ThrowsAsync<IOException>(() => broken.PutAsync("quarantine/x", new byte[] { 0xFF, 0xD8, 0xFF }, CancellationToken.None));
    }

    [Fact]
    public async Task A_signed_link_downloads_the_file_straight_from_storage()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 42 };
        await _store.PutAsync("archive/signed.png", png, CancellationToken.None);

        var url = await _store.SignedReadUrlAsync("archive/signed.png", TimeSpan.FromMinutes(15), CancellationToken.None);

        Assert.NotNull(url);
        Assert.StartsWith($"{_emulatorHost}/test-archive/signed.png?", url!.ToString(), StringComparison.Ordinal);
        Assert.Contains("X-Goog-Signature=", url.Query, StringComparison.Ordinal);
        using var http = new HttpClient();
        Assert.Equal(png, await http.GetByteArrayAsync(url));
    }

    [Fact]
    public async Task The_api_stores_photos_in_the_quarantine_bucket_and_its_file_links_redirect_there()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"gcs_api_{Guid.NewGuid():N}");
        Assert.True(Migrator.Upgrade(connectionString).Successful);
        await using var db = NpgsqlDataSource.Create(connectionString);
        await using var factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            foreach (var (key, value) in Settings())
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        var api = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var artistId = await ScoringSetup.ActiveArtistAsync(db, "sp-gcs", "GCS Band", releases: 1);

        var session = await api.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken("g-gcs_fan", "gcs_fan@example.test"), handle = "gcs_fan" });
        var token = (await Json(session)).GetProperty("access_token").GetString()!;
        var photo = JpegWithLocation();
        var intent = await Json(await Send(api, HttpMethod.Post, "/v1/uploads", token, new { purpose = "submission", mime_type = "image/jpeg", bytes = photo.Length }));
        var put = new ByteArrayContent(photo);
        put.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        Assert.Equal(HttpStatusCode.NoContent, (await api.PutAsync(intent.GetProperty("url").GetString(), put)).StatusCode);
        var uploadId = intent.GetProperty("upload_id").GetString()!;
        Assert.NotNull(await _client.GetObjectAsync("test-quarantine", uploadId));

        var submission = await Json(await Send(api, HttpMethod.Post, "/v1/submissions", token, new
        {
            artist_id = artistId, kind = "concert_photo", upload_ids = new[] { uploadId },
            concert = new { date = "2026-10-03", venue = "Metro", city = "Chicago" },
        }));
        var fileUrl = submission.GetProperty("files")[0].GetProperty("url").GetString()!;

        var link = await api.GetAsync(fileUrl);
        Assert.Equal(HttpStatusCode.Redirect, link.StatusCode);
        var location = link.Headers.Location!;
        Assert.StartsWith($"{_emulatorHost}/test-quarantine/{uploadId}?", location.ToString(), StringComparison.Ordinal);
        using var http = new HttpClient();
        var stored = await http.GetByteArrayAsync(location);
        Assert.DoesNotContain("41.8781N", Encoding.ASCII.GetString(stored), StringComparison.Ordinal);
        Assert.Contains("crowd", Encoding.ASCII.GetString(stored), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private Dictionary<string, string?> Settings(string? quarantine = null) => new()
    {
        ["Storage:Provider"] = "gcs",
        ["Storage:QuarantineBucket"] = quarantine ?? Buckets.QuarantineBucket,
        ["Storage:ArchiveBucket"] = Buckets.ArchiveBucket,
        ["Storage:MediaBucket"] = Buckets.MediaBucket,
        ["Storage:EmulatorHost"] = _emulatorHost,
    };

    private static IFileStore StoreFor(IConfiguration configuration) =>
        new ServiceCollection().AddFileStore(configuration, Path.GetTempPath()).BuildServiceProvider().GetRequiredService<IFileStore>();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A minimal JPEG with a location in its EXIF and comment segments, which the API must strip.</summary>
    private static byte[] JpegWithLocation()
    {
        using var jpeg = new MemoryStream();
        void Segment(byte type, byte[] payload)
        {
            jpeg.Write([0xFF, type, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            jpeg.Write(payload);
        }

        jpeg.Write([0xFF, 0xD8]);
        Segment(0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(0xE1, [.. "Exif\0\0"u8, .. "GPS 41.8781N 87.6298W"u8]);
        Segment(0xDB, new byte[65]);
        Segment(0xDA, [1, 1, 0, 0, 63, 0]);
        jpeg.Write("crowd"u8);
        jpeg.Write([0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    private static Task<HttpResponseMessage> Send(HttpClient api, HttpMethod method, string path, string token, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return api.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
