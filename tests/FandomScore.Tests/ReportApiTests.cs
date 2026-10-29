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

/// <summary>Reporting feed posts and staff takedowns (migration 0014).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"report-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"report_api_{Guid.NewGuid():N}");
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
    }

    [Fact]
    public async Task Reported_logs_are_grouped_for_staff_and_a_takedown_hides_the_log_and_reverses_its_points()
    {
        var author = await SignInAsync("author");
        var first = await SignInAsync("fan_one");
        var second = await SignInAsync("fan_two");
        var staff = await StaffAsync("staffer");
        var logId = await LogAsync("author", "public");

        await ReportAsync(first, "log", logId, "abuse", "Insults another fan");
        await ReportAsync(first, "log", logId, "spam");   // the same fan again: still one open report
        await ReportAsync(second, "log", logId, "abuse");

        var queue = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/reports", staff), HttpStatusCode.OK);
        var post = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal(("log", 2, "author"), (post.GetProperty("target_type").GetString(), post.GetProperty("report_count").GetInt32(),
            post.GetProperty("author").GetProperty("handle").GetString()));
        Assert.Equal(("abuse", 2), (post.GetProperty("reasons")[0].GetProperty("reason").GetString(), post.GetProperty("reasons")[0].GetProperty("count").GetInt32()));
        Assert.Equal(["Insults another fan"], post.GetProperty("details").EnumerateArray().Select(d => d.GetString()));
        Assert.StartsWith("Takedown", post.GetProperty("log").GetProperty("review").GetString(), StringComparison.Ordinal);

        var decision = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/reports/log/{logId}/decision", staff,
            new { action = "take_down", note = "Abusive" }), HttpStatusCode.OK);
        Assert.Equal(2, decision.GetProperty("reports_resolved").GetInt32());

        // Gone for everyone else, kept for its author; its points are reversed; the decision is on record.
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed", first), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/logs/{logId}", first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/v1/logs/{logId}", author)).StatusCode);
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/reports", staff), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        await using var connection = await _db.OpenConnectionAsync();
        Assert.False(await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM score_events WHERE source_id = @logId AND reversed_at IS NULL)", new { logId }));
        Assert.Equal(("take_down", "Abusive", 2), await connection.QuerySingleAsync<(string, string, int)>(
            "SELECT action, note, reports FROM moderation_actions WHERE target_id = @logId", new { logId }));
    }

    [Fact]
    public async Task A_dismissed_photo_stays_and_a_taken_down_photo_leaves_the_feed_for_good()
    {
        var author = await SignInAsync("author");
        var fan = await SignInAsync("fan_one");
        var staff = await StaffAsync("staffer");
        var photoId = await SharedPhotoAsync("author");

        await ReportAsync(fan, "photo", photoId, "inappropriate");
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/reports/photo/{photoId}/decision", staff, new { action = "dismiss" }),
            HttpStatusCode.OK);
        Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed", fan), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());

        // Reports closed by a decision don't block a new one.
        await ReportAsync(fan, "photo", photoId, "inappropriate", "Still not OK");
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/reports/photo/{photoId}/decision", staff, new { action = "take_down" }),
            HttpStatusCode.OK);
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed", fan), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/submissions/{photoId}", author), HttpStatusCode.OK);
        Assert.Equal((true, false, "verified"), (mine.GetProperty("removed").GetBoolean(), mine.GetProperty("can_share").GetBoolean(),
            mine.GetProperty("status").GetString()));
        var reshare = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/submissions/{photoId}/share", author, new { shared = true }),
            HttpStatusCode.Conflict);
        Assert.Contains("took this photo down", reshare.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_feed_link_to_a_photo_stops_at_its_takedown_but_the_owner_and_staff_links_keep_working()
    {
        var author = await SignInAsync("author");
        var fan = await SignInAsync("fan_one");
        var staff = await StaffAsync("staffer");
        var photoId = await SharedPhotoAsync("author");
        var bytes = await ArchivedFileAsync(photoId);

        var feed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed", fan), HttpStatusCode.OK);
        var feedLink = Assert.Single(FileLinks(feed));
        var ownerLink = Assert.Single(FileLinks(await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/submissions/{photoId}", author), HttpStatusCode.OK)));
        await ReportAsync(fan, "photo", photoId, "inappropriate");
        var reportLink = Assert.Single(FileLinks(await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/reports", staff), HttpStatusCode.OK)));
        Assert.NotEqual(feedLink, ownerLink);   // the feed gets its own kind of link
        Assert.Equal(bytes, await FetchFileAsync(feedLink));

        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/reports/photo/{photoId}/decision", staff, new { action = "take_down" }),
            HttpStatusCode.OK);

        // The feed link, still within its hour, is refused at once; anyone could have copied it.
        var gone = await _client.GetAsync(new Uri(feedLink).PathAndQuery);
        Assert.Equal("not_found", (await JsonAsync(gone, HttpStatusCode.NotFound)).GetProperty("code").GetString());

        // The photo stays as proof: its owner and staff still see it, with links handed out before and after.
        Assert.Equal(bytes, await FetchFileAsync(ownerLink));
        Assert.Equal(bytes, await FetchFileAsync(reportLink));
        Assert.Equal(bytes, await FetchFileAsync(Assert.Single(FileLinks(
            await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/submissions/{photoId}", author), HttpStatusCode.OK)))));
        Assert.Equal(bytes, await FetchFileAsync(Assert.Single(FileLinks(
            await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/submissions/{photoId}", staff), HttpStatusCode.OK)))));
    }

    [Fact]
    public async Task A_feed_link_to_a_photo_stops_when_its_author_unshares_it()
    {
        await SignInAsync("author");
        var fan = await SignInAsync("fan_one");
        var photoId = await SharedPhotoAsync("author");
        var bytes = await ArchivedFileAsync(photoId);
        var feedLink = Assert.Single(FileLinks(await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed", fan), HttpStatusCode.OK)));
        Assert.Equal(bytes, await FetchFileAsync(feedLink));

        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE submissions SET shared_at = NULL WHERE id = @photoId", new { photoId });
        }

        await JsonAsync(await _client.GetAsync(new Uri(feedLink).PathAndQuery), HttpStatusCode.NotFound);

        // A tampered link is still refused as invalid, not looked up.
        var forged = feedLink.Replace("sig=", "sig=x", StringComparison.Ordinal);
        await JsonAsync(await _client.GetAsync(new Uri(forged).PathAndQuery), HttpStatusCode.Forbidden);
    }

    /// <summary>Every <c>/v1/files/</c> link anywhere in a response.</summary>
    private static List<string> FileLinks(JsonElement json)
    {
        var links = new List<string>();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item);
                    break;
                case JsonValueKind.String when e.GetString()!.Contains("/v1/files/", StringComparison.Ordinal):
                    links.Add(e.GetString()!);
                    break;
            }
        }

        Walk(json);
        return links;
    }

    private async Task<byte[]> FetchFileAsync(string link)
    {
        var response = await _client.GetAsync(new Uri(link).PathAndQuery);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{link}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>A redacted, archived file on the photo (only those reach the feed), stored in the test's local store.</summary>
    private async Task<byte[]> ArchivedFileAsync(Guid submissionId)
    {
        var key = $"archive/{Guid.NewGuid()}";
        var bytes = "test-photo-bytes"u8.ToArray();
        await _factory.Services.GetRequiredService<FandomScore.Storage.IFileStore>().PutAsync(key, bytes, CancellationToken.None);
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes, tier, redacted, redaction_state, archived_at)
            VALUES (@submissionId, @key, @sha, 'image/jpeg', @length, 'archive', true, 'archived', now())
            """,
            new { submissionId, key, sha = System.Security.Cryptography.SHA256.HashData(bytes), length = (long)bytes.Length });
        return bytes;
    }

    [Fact]
    public async Task Only_visible_posts_by_others_can_be_reported_and_only_staff_decide()
    {
        var author = await SignInAsync("author");
        var fan = await SignInAsync("fan_one");
        var staff = await StaffAsync("staffer");
        var publicLog = await LogAsync("author", "public");
        var privateLog = await LogAsync("author", "private");

        Assert.Equal("target_id", Field(await JsonAsync(await ReportRawAsync(author, "log", publicLog, "spam"), HttpStatusCode.UnprocessableEntity)));
        await JsonAsync(await ReportRawAsync(fan, "log", privateLog, "spam"), HttpStatusCode.NotFound);
        await JsonAsync(await ReportRawAsync(fan, "log", Guid.NewGuid(), "spam"), HttpStatusCode.NotFound);
        Assert.Equal("reason", Field(await JsonAsync(await ReportRawAsync(fan, "log", publicLog, "boring"), HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("target_type", Field(await JsonAsync(await ReportRawAsync(fan, "user", publicLog, "spam"), HttpStatusCode.UnprocessableEntity)));

        await ReportAsync(fan, "log", publicLog, "spam");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/v1/admin/reports", fan)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/v1/admin/reports/log/{publicLog}/decision", fan,
            new { action = "take_down" })).StatusCode);
        Assert.Equal("action", Field(await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/reports/log/{publicLog}/decision", staff,
            new { action = "delete" }), HttpStatusCode.UnprocessableEntity)));
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/reports/log/{privateLog}/decision", staff,
            new { action = "dismiss" }), HttpStatusCode.NotFound);
    }

    private static string Field(JsonElement problem) => problem.GetProperty("errors")[0].GetProperty("field").GetString()!;

    private async Task<Guid> LogAsync(string handle, string visibility)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO album_logs (user_id, album_id, rating, review, visibility)
            SELECT u.id, al.id, 2, 'Takedown candidate review that is long enough to earn the review bonus points here, yes.', @visibility::visibility
            FROM users u, albums al WHERE u.handle = @handle AND al.artist_id = @_artistId RETURNING id
            """,
            new { handle, visibility, _artistId });
    }

    private async Task<Guid> SharedPhotoAsync(string handle)
    {
        await using var connection = await _db.OpenConnectionAsync();
        var id = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO submissions (user_id, artist_id, kind) SELECT id, @_artistId, 'merch' FROM users WHERE handle = @handle RETURNING id",
            new { handle, _artistId });
        await connection.ExecuteAsync(
            "UPDATE submissions SET status = 'verified' WHERE id = @id; UPDATE submissions SET shared_at = now() WHERE id = @id;", new { id });
        return id;
    }

    private async Task ReportAsync(string token, string targetType, Guid targetId, string reason, string? details = null) =>
        Assert.Equal(HttpStatusCode.Accepted, (await ReportRawAsync(token, targetType, targetId, reason, details)).StatusCode);

    private Task<HttpResponseMessage> ReportRawAsync(string token, string targetType, Guid targetId, string reason, string? details = null) =>
        SendAsync(HttpMethod.Post, "/v1/reports", token, new { target_type = targetType, target_id = targetId, reason, details });

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
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }
}
