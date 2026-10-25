using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>Photo uploads, proof submissions, staff review, sharing, and the home feed (migration 0012).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProofApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string GpsSecret = "GPS 41.8781N 87.6298W";

    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"proof-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"proof_api_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
    public async Task A_concert_photo_goes_from_upload_to_review_to_points_to_the_feed()
    {
        var fan = await SignInAsync("fan_one");
        var staff = await StaffAsync("staffer");
        var photo = Jpeg("crowd");

        var uploadId = await UploadAsync(fan, photo);
        var submitted = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/submissions", fan,
            new
            {
                artist_id = _artistId, kind = "concert_photo", upload_ids = new[] { uploadId }, caption = "Front row!",
                concert = new { date = "2026-10-03", venue = "Metro", city = "Chicago" },
            }), HttpStatusCode.Created);
        var id = submitted.GetProperty("id").GetString();
        Assert.Equal("needs_review", submitted.GetProperty("status").GetString());
        Assert.False(submitted.GetProperty("can_share").GetBoolean());

        // Stored without its metadata: the GPS text is gone, the image data is intact.
        var file = Assert.Single(submitted.GetProperty("files").EnumerateArray());
        var stored = await _client.GetByteArrayAsync(file.GetProperty("url").GetString());
        Assert.DoesNotContain(GpsSecret, Encoding.ASCII.GetString(stored), StringComparison.Ordinal);
        Assert.Contains("crowd", Encoding.ASCII.GetString(stored), StringComparison.Ordinal);

        var queue = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/reviews", staff), HttpStatusCode.OK);
        var queued = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal("fan_one", queued.GetProperty("user").GetProperty("handle").GetString());

        var approved = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/submissions/{id}/decision", staff,
            new { decision = "approve", notes = "Clearly at the show" }), HttpStatusCode.OK);
        Assert.Equal("verified", approved.GetProperty("status").GetString());
        var award = Assert.Single(approved.GetProperty("points_awarded").EnumerateArray());
        Assert.Equal(("concert_photo_verified", 5m), (award.GetProperty("rule_key").GetString(), award.GetProperty("points").GetDecimal()));
        Assert.Equal("manual", Assert.Single(approved.GetProperty("checks").EnumerateArray()).GetProperty("check_type").GetString());

        var shared = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/submissions/{id}/share", fan, new { shared = true }), HttpStatusCode.OK);
        Assert.NotEqual(JsonValueKind.Null, shared.GetProperty("shared_at").ValueKind);

        var feed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed", fan), HttpStatusCode.OK);
        var item = Assert.Single(feed.GetProperty("items").EnumerateArray());
        var concert = item.GetProperty("photo").GetProperty("concert");
        Assert.Equal(("Metro", "Chicago", "2026-10-03"), (concert.GetProperty("venue").GetString(), concert.GetProperty("city").GetString(),
            concert.GetProperty("date").GetString()));
        Assert.Equal(("photo", "Front row!", "fan_one", "Geese"), (item.GetProperty("type").GetString(),
            item.GetProperty("photo").GetProperty("caption").GetString(), item.GetProperty("user").GetProperty("handle").GetString(),
            item.GetProperty("artist").GetProperty("name").GetString()));

        // A later rejection reverses the points and takes the photo off the feed.
        var rejected = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/submissions/{id}/decision", staff,
            new { decision = "reject", reason = "Not from this artist's show" }), HttpStatusCode.OK);
        Assert.Empty(rejected.GetProperty("points_awarded").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, rejected.GetProperty("shared_at").ValueKind);
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed", fan), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_feed_mixes_shared_photos_and_public_album_logs()
    {
        var fan = await SignInAsync("fan_one");
        var other = await SignInAsync("fan_two");
        var staff = await StaffAsync("staffer");
        var merch = await SubmitAsync(other, "merch", Png());
        await DecideAsync(staff, merch, "approve");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/submissions/{merch}/share", other, new { shared = true }), HttpStatusCode.OK);

        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO album_logs (user_id, album_id, rating, review, visibility, created_at)
                  SELECT u.id, al.id, 9, repeat('Loud and lovely. ', 30), 'public', now() + interval '1 minute'
                  FROM users u, albums al WHERE u.handle = 'fan_one' AND al.artist_id = @_artistId;
                INSERT INTO album_logs (user_id, album_id, visibility)
                  SELECT u.id, al.id, 'private' FROM users u, albums al WHERE u.handle = 'fan_one' AND al.artist_id = @_artistId;
                """,
                new { _artistId });
        }

        // The feed is a shuffled mix (FeedApiTests covers the order), so look items up by type.
        var feed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed", fan), HttpStatusCode.OK);
        var items = feed.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["album_log", "photo"], items.Select(i => i.GetProperty("type").GetString()).Order());
        var log = items.Single(i => i.GetProperty("type").GetString() == "album_log").GetProperty("log");
        Assert.Equal(9, log.GetProperty("rating").GetInt32());
        Assert.True(log.GetProperty("review_truncated").GetBoolean());
        Assert.Equal("merch", items.Single(i => i.GetProperty("type").GetString() == "photo").GetProperty("photo").GetProperty("kind").GetString());

        // Deleted accounts drop out.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET deleted_at = now() WHERE handle = 'fan_two'");
        }

        var after = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed", fan), HttpStatusCode.OK);
        Assert.Equal(["album_log"], after.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Concert_photos_are_pinned_to_one_of_the_bands_shows_or_a_concert_the_fan_enters()
    {
        var fan = await SignInAsync("fan_one");
        Guid ownShow, otherShow;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            var other = await ScoringSetup.ActiveArtistAsync(_db, "sp-other", "Other Band", releases: 1);
            ownShow = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO events (artist_id, name, venue, city, starts_at) VALUES (@_artistId, 'Geese at Metro', 'Metro', 'Chicago', now() - interval '2 days') RETURNING id",
                new { _artistId });
            otherShow = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO events (artist_id, name, venue, city, starts_at) VALUES (@other, 'Other Band live', 'Metro', 'Chicago', now() - interval '2 days') RETURNING id",
                new { other });
        }

        async Task<JsonElement> PostAsync(object fields, HttpStatusCode expected)
        {
            var body = new Dictionary<string, object?> { ["artist_id"] = _artistId, ["kind"] = "concert_photo", ["upload_ids"] = new[] { await UploadAsync(fan, Jpeg(Guid.NewGuid().ToString())) } };
            foreach (var (key, value) in JsonSerializer.SerializeToElement(fields).EnumerateObject().Select(p => (p.Name, (object?)p.Value)))
            {
                body[key] = value;
            }

            return await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/submissions", fan, body), expected);
        }

        string Field(JsonElement problem) => problem.GetProperty("errors")[0].GetProperty("field").GetString()!;

        Assert.Equal("event_id", Field(await PostAsync(new { }, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("event_id", Field(await PostAsync(new { event_id = otherShow }, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("concert.date", Field(await PostAsync(
            new { concert = new { date = "2999-01-01", venue = "Metro", city = "Chicago" } }, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("concert.city", Field(await PostAsync(
            new { concert = new { date = "2026-10-03", venue = "Metro" } }, HttpStatusCode.UnprocessableEntity)));

        var pinned = await PostAsync(new { event_id = ownShow }, HttpStatusCode.Created);
        Assert.Equal("Geese at Metro", pinned.GetProperty("event").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, pinned.GetProperty("concert").ValueKind);

        // Merch doesn't take a concert.
        var merch = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/submissions", fan, new
        {
            artist_id = _artistId, kind = "merch", upload_ids = new[] { await UploadAsync(fan, Jpeg("shirt")) },
            concert = new { date = "2026-10-03", venue = "Metro", city = "Chicago" },
        }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("concert", Field(merch));
    }

    [Fact]
    public async Task Tickets_can_be_proof_but_never_shared_and_unverified_photos_wait()
    {
        var fan = await SignInAsync("fan_one");
        var staff = await StaffAsync("staffer");
        var ticket = await SubmitAsync(fan, "ticket", Jpeg("ticket"));
        var pending = await SubmitAsync(fan, "merch", Jpeg("hoodie"));
        await DecideAsync(staff, ticket, "approve");

        var noTicket = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/submissions/{ticket}/share", fan, new { shared = true }), HttpStatusCode.Conflict);
        Assert.Contains("barcodes", noTicket.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var notYet = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/submissions/{pending}/share", fan, new { shared = true }), HttpStatusCode.Conflict);
        Assert.Contains("still being checked", notYet.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_submissions_can_be_withdrawn_and_their_files_go_too()
    {
        var fan = await SignInAsync("fan_one");
        var staff = await StaffAsync("staffer");
        var id = await SubmitAsync(fan, "merch", Jpeg("tee"));
        var url = (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/submissions/{id}", fan), HttpStatusCode.OK))
            .GetProperty("files")[0].GetProperty("url").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/submissions/{id}", fan)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(url)).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(_storage, "*", SearchOption.AllDirectories));

        var verified = await SubmitAsync(fan, "merch", Jpeg("cap"));
        await DecideAsync(staff, verified, "approve");
        await JsonAsync(await SendAsync(HttpMethod.Delete, $"/v1/submissions/{verified}", fan), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Uploads_are_checked_and_duplicates_are_refused()
    {
        var fan = await SignInAsync("fan_one");
        var bad = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", fan,
            new { purpose = "submission", mime_type = "image/heic", bytes = 10 }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("mime_type", bad.GetProperty("errors")[0].GetProperty("field").GetString());
        var wrongType = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", fan,
            new { purpose = 1, mime_type = true, bytes = 10 }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal(["purpose", "mime_type"], wrongType.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()));

        // Not an image, bigger than declared, a forged link, and a wrong declared hash.
        var notImage = await IntentAsync(fan, 100);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAsync(notImage, "just text"u8.ToArray())).StatusCode);
        var tooBig = await IntentAsync(fan, 5);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAsync(tooBig, Jpeg("big"))).StatusCode);
        var forged = (await IntentAsync(fan, 500)).Replace("sig=", "sig=x", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(forged, Jpeg("x"))).StatusCode);
        var photo = Jpeg("same");
        var wrongHash = await IntentAsync(fan, photo.Length, Convert.ToHexStringLower(SHA256.HashData("other"u8)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAsync(wrongHash, photo)).StatusCode);

        // The same photo again is a duplicate; an upload can't be used twice.
        var first = await UploadAsync(fan, photo);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/submissions", fan,
            new { artist_id = _artistId, kind = "merch", upload_ids = new[] { first } }), HttpStatusCode.Created);
        var again = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/submissions", fan,
            new { artist_id = _artistId, kind = "merch", upload_ids = new[] { await UploadAsync(fan, photo) } }), HttpStatusCode.Conflict);
        Assert.Equal("duplicate_submission", again.GetProperty("code").GetString());
        var reused = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/submissions", fan,
            new { artist_id = _artistId, kind = "merch", upload_ids = new[] { first } }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("upload_ids", reused.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Reviewing_is_for_staff_and_rejections_need_a_reason()
    {
        var fan = await SignInAsync("fan_one");
        var staff = await StaffAsync("staffer");
        var id = await SubmitAsync(fan, "merch", Jpeg("pin"));

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/v1/admin/reviews", fan)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/v1/admin/submissions/{id}/decision", fan,
            new { decision = "approve" })).StatusCode);
        var noReason = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/submissions/{id}/decision", staff,
            new { decision = "reject" }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("reason", noReason.GetProperty("errors")[0].GetProperty("field").GetString());
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/submissions/{id}/decision", staff, new { decision = 1 }),
            HttpStatusCode.UnprocessableEntity);
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/submissions/{Guid.NewGuid()}/decision", staff,
            new { decision = "approve" }), HttpStatusCode.NotFound);

        // Other fans can't see someone's submission.
        var other = await SignInAsync("fan_two");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/submissions/{id}", other)).StatusCode);
    }

    /// <summary>A minimal JPEG: JFIF, an EXIF segment holding <see cref="GpsSecret"/>, a table, and scan data with <paramref name="marker"/>.</summary>
    private static byte[] Jpeg(string marker)
    {
        using var jpeg = new MemoryStream();
        void Segment(byte type, byte[] payload)
        {
            jpeg.Write([0xFF, type, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            jpeg.Write(payload);
        }

        jpeg.Write([0xFF, 0xD8]);
        Segment(0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Segment(0xE1, [.. "Exif\0\0"u8, .. Encoding.ASCII.GetBytes(GpsSecret)]);
        Segment(0xFE, Encoding.ASCII.GetBytes("comment " + GpsSecret));
        Segment(0xDB, new byte[65]);
        Segment(0xDA, [1, 1, 0, 0, 63, 0]);
        jpeg.Write(Encoding.ASCII.GetBytes(marker));
        jpeg.Write([0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    /// <summary>A minimal PNG with a tEXt chunk holding <see cref="GpsSecret"/> (CRCs aren't checked).</summary>
    private static byte[] Png()
    {
        using var png = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            png.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            png.Write(Encoding.ASCII.GetBytes(type));
            png.Write(data);
            png.Write([0, 0, 0, 0]);
        }

        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk("IHDR", new byte[13]);
        Chunk("tEXt", Encoding.ASCII.GetBytes("Location\0" + GpsSecret));
        Chunk("IDAT", [1, 2, 3]);
        Chunk("IEND", []);
        return png.ToArray();
    }

    private async Task<string> SubmitAsync(string token, string kind, byte[] photo)
    {
        var uploadId = await UploadAsync(token, photo, photo[0] == 0x89 ? "image/png" : "image/jpeg");
        var created = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/submissions", token,
            new { artist_id = _artistId, kind, upload_ids = new[] { uploadId } }), HttpStatusCode.Created);
        return created.GetProperty("id").GetString()!;
    }

    private async Task DecideAsync(string staff, string id, string decision) =>
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/submissions/{id}/decision", staff,
            new { decision, reason = decision == "reject" ? "No" : null }), HttpStatusCode.OK);

    private async Task<string> UploadAsync(string token, byte[] photo, string mimeType = "image/jpeg")
    {
        var intent = await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", token,
            new { purpose = "submission", mime_type = mimeType, bytes = photo.Length }), HttpStatusCode.Created);
        var response = await PutAsync(intent.GetProperty("url").GetString()!, photo, mimeType);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return intent.GetProperty("upload_id").GetString()!;
    }

    private async Task<string> IntentAsync(string token, int bytes, string? sha256 = null) =>
        (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/uploads", token,
            new { purpose = "submission", mime_type = "image/jpeg", bytes, sha256 }), HttpStatusCode.Created)).GetProperty("url").GetString()!;

    // The signed upload link needs no sign-in, like a storage provider's presigned URL.
    private Task<HttpResponseMessage> PutAsync(string url, byte[] content, string mimeType = "image/jpeg")
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        return _client.PutAsync(url, body);
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
