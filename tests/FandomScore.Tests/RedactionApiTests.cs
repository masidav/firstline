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

/// <summary>Staff decisions on files the redaction worker sent to them (GET/POST /admin/redactions, migration 0029).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RedactionApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"redaction-api-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"redaction_api_{Guid.NewGuid():N}");
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
    public async Task Staff_see_files_waiting_and_can_archive_or_discard_them()
    {
        var fan = await SignInAsync("fan");
        var staff = await StaffAsync("staffer");
        var (submission, archiveMe, _) = await WaitingFileAsync("fan", "ticket", "No barcode or QR code was found on this ticket. Check it by eye.");
        var (_, discardMe, discardKey) = await WaitingFileAsync("fan", "screenshot", "The image couldn't be read.");

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/v1/admin/redactions", fan)).StatusCode);

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/redactions", staff), HttpStatusCode.OK);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([archiveMe, discardMe], items.Select(i => i.GetProperty("file_id").GetGuid()));
        var first = items[0];
        Assert.Equal(("ticket", "fan", "Geese"), (first.GetProperty("kind").GetString(),
            first.GetProperty("user").GetProperty("handle").GetString(), first.GetProperty("artist").GetProperty("name").GetString()));
        Assert.StartsWith("No barcode", first.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal(first.GetProperty("uploaded_at").GetDateTime().AddDays(30), first.GetProperty("deleted_after").GetDateTime());
        Assert.Contains("/v1/files/", first.GetProperty("file").GetProperty("url").GetString(), StringComparison.Ordinal);

        // Archive: approved, and the worker is queued to redact what it finds and archive it.
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/redactions/{archiveMe}/decision", staff, new { action = "archive" }),
            HttpStatusCode.NoContent);
        Assert.Equal("approved", await ScalarAsync<string>("SELECT redaction_state FROM submission_files WHERE id = @archiveMe", new { archiveMe }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*) FROM jobs WHERE kind = 'submissions.redact' AND status = 'queued' AND payload->>'submission_id' = @id",
            new { id = submission.ToString() }));
        var again = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/redactions/{archiveMe}/decision", staff, new { action = "archive" }),
            HttpStatusCode.Conflict);
        Assert.Equal("conflict", again.GetProperty("code").GetString());

        // Discard: the file goes; the submission stays verified.
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/redactions/{discardMe}/decision", staff, new { action = "discard" }),
            HttpStatusCode.NoContent);
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM submission_files WHERE id = @discardMe", new { discardMe }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM uploads WHERE storage_key = @discardKey", new { discardKey }));
        Assert.False(File.Exists(Path.Combine(_storage, discardKey)));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*) FROM storage_deletions"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*) FROM submissions WHERE status = 'verified'"));

        var empty = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/redactions", staff), HttpStatusCode.OK);
        Assert.Empty(empty.GetProperty("items").EnumerateArray());

        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/redactions/{Guid.NewGuid()}/decision", staff, new { action = "discard" }),
            HttpStatusCode.NotFound);
        var invalid = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/redactions/{archiveMe}/decision", staff, new { action = "keep" }),
            HttpStatusCode.UnprocessableEntity);
        Assert.Equal("validation_failed", invalid.GetProperty("code").GetString());
    }

    /// <summary>A verified submission whose one file the worker sent to staff.</summary>
    private async Task<(Guid Submission, Guid File, string Key)> WaitingFileAsync(string handle, string kind, string note)
    {
        var key = $"quarantine/{Guid.NewGuid()}";
        Directory.CreateDirectory(Path.Combine(_storage, "quarantine"));
        await File.WriteAllBytesAsync(Path.Combine(_storage, key), [1, 2, 3]);
        await using var connection = await _db.OpenConnectionAsync();
        var submission = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO submissions (user_id, artist_id, kind) SELECT id, @_artistId, @kind::submission_kind FROM users WHERE handle = @handle RETURNING id",
            new { _artistId, kind, handle });
        await connection.ExecuteAsync(
            """
            INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes, storage_key, sha256, bytes, uploaded_at, submission_id)
            SELECT id, 'submission', 'image/jpeg', 3, @key, sha256(@key::bytea), 3, now(), @submission FROM users WHERE handle = @handle
            """,
            new { key, submission, handle });
        var file = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes, uploaded_at)
            VALUES (@submission, @key, sha256(@key::bytea), 'image/jpeg', 3, clock_timestamp()) RETURNING id
            """,
            new { submission, key });
        await connection.ExecuteAsync(
            """
            UPDATE submissions SET status = 'verified' WHERE id = @submission;
            DELETE FROM jobs;
            UPDATE submission_files SET redaction_state = 'needs_review', redaction_note = @note WHERE id = @file;
            """,
            new { submission, note, file });
        return (submission, file, key);
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
