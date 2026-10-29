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

/// <summary>"Download my data" (<c>GET /me/export</c>): the caller's own data, nothing secret, nothing of other fans'.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DataExportApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] Chicago = ["us-il-chicago"];

    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"export-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"export_{Guid.NewGuid():N}");
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
    public async Task The_export_holds_the_callers_data_and_nothing_secret_or_belonging_to_other_fans()
    {
        var (ana, anaRefresh) = await SignInWithRefreshAsync("ana");
        var ben = (await SignInWithRefreshAsync("ben")).Access;
        await SignInWithRefreshAsync("zed");
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", ana, new { city_ids = Chicago }), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", ben, new { city_ids = Chicago }), HttpStatusCode.OK);
        var allocationId = await AllocationAsync();
        await ScoreAsync("ana", 90);
        await ScoreAsync("ben", 80);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", ana, new { plus_one = true }), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration/guest", ana, new { handle = "ben" }), HttpStatusCode.OK);
        await SeedAsync();
        await Task.Delay(TimeSpan.FromSeconds(2.5));   // registration closes
        var staff = await SignInWithRefreshAsync("drawer");
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = 'drawer'");
        }

        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/selection", staff.Access), HttpStatusCode.Accepted);
        await Draws.RunQueuedAsync(_db);

        var response = await SendAsync(HttpMethod.Get, "/v1/me/export", ana);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = JsonDocument.Parse(text).RootElement;
        if (Environment.GetEnvironmentVariable("DATA_EXPORT_PREVIEW") is { Length: > 0 } preview)
        {
            await File.WriteAllTextAsync(preview, text);
        }

        // Own data, by area.
        Assert.Equal(1, export.GetProperty("format_version").GetInt32());
        var account = export.GetProperty("account");
        Assert.Equal(("ana", "ana@example.test"), (account.GetProperty("profile").GetProperty("handle").GetString(),
            account.GetProperty("profile").GetProperty("email").GetString()));
        Assert.Equal("google", Single(account, "sign_in_methods").GetProperty("provider").GetString());
        Assert.True(Single(account, "sessions").GetProperty("current").GetBoolean());
        Assert.Equal("us-il-chicago", Single(account, "hub_cities").GetProperty("city_id").GetString());
        Assert.Equal("ios", Single(account, "push_devices").GetProperty("platform").GetString());
        var spotify = export.GetProperty("spotify");
        Assert.Equal("sp-user-ana", spotify.GetProperty("connection").GetProperty("spotify_user_id").GetString());
        Assert.Equal(7, Single(spotify, "artist_signals").GetProperty("liked_tracks").GetInt32());
        Assert.Equal("Ana Track", Single(spotify, "saved_tracks").GetProperty("track_title").GetString());
        Assert.Equal("Album 1", Single(spotify, "saved_albums").GetProperty("album_title").GetString());
        Assert.Equal("Ana Track", Single(spotify, "plays").GetProperty("track_title").GetString());
        var fandom = export.GetProperty("fandom");
        Assert.Equal(90m, Single(fandom, "scores").GetProperty("percentile").GetDecimal());
        Assert.Equal("Geese", Single(fandom, "points").GetProperty("artist_name").GetString());
        var journal = export.GetProperty("journal");
        Assert.Equal("Ana's review of Album 1", Single(journal, "album_logs").GetProperty("review").GetString());
        var activity = export.GetProperty("activity");
        Assert.Equal("Ana's answer", Single(activity, "prompt_answers").GetProperty("body").GetString());
        var trivia = activity.GetProperty("trivia").EnumerateArray().ToList();
        Assert.Equal(2, trivia.Count);
        Assert.Equal(1, trivia[0].GetProperty("answer_index").GetInt32());                    // answered: the right answer shows
        Assert.Equal(JsonValueKind.Null, trivia[1].GetProperty("answer_index").ValueKind);   // still open: it doesn't
        Assert.Equal("fire", Single(activity, "reactions").GetProperty("reaction").GetString());
        Assert.Equal("Ana's report", Single(activity, "reports_filed").GetProperty("details").GetString());
        var submission = Single(export.GetProperty("proof"), "submissions");
        Assert.Equal("Ana at the Metro", submission.GetProperty("caption").GetString());
        var file = Assert.Single(submission.GetProperty("files").EnumerateArray());
        Assert.Contains("/v1/files/", file.GetProperty("url").GetString(), StringComparison.Ordinal);
        var social = export.GetProperty("social");
        Assert.Equal("ben", Single(social, "following").GetProperty("handle").GetString());
        Assert.Equal("ben", Single(social, "followers").GetProperty("handle").GetString());
        Assert.Equal("Ana Grab", Single(social, "grab_bag").GetProperty("title").GetString());
        var tickets = export.GetProperty("tickets");
        var registration = Single(tickets, "registrations");
        Assert.Equal(("ben", "selected", 2), (registration.GetProperty("guest_handle").GetString(),
            registration.GetProperty("outcome").GetString(), registration.GetProperty("seats").GetInt32()));
        Assert.Equal(2, Single(tickets, "reservations").GetProperty("seats").GetInt32());
        Assert.Contains(export.GetProperty("notifications").EnumerateArray(), n => n.GetProperty("kind").GetString() == "draw_selected");
        Assert.DoesNotContain(export.GetProperty("notifications").EnumerateArray(), n => n.GetProperty("kind").GetString()!.StartsWith("staff_", StringComparison.Ordinal));

        // No secrets, tokens, hashes, or storage keys.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            var reservationToken = await connection.ExecuteScalarAsync<string>("SELECT token FROM reservations") ?? throw new InvalidOperationException("no reservation");
            var refreshHashes = (await connection.QueryAsync<byte[]>(
                "SELECT t.token_hash FROM refresh_tokens t JOIN auth_sessions s ON s.id = t.session_id JOIN users u ON u.id = s.user_id WHERE u.handle = 'ana'")).ToList();
            Assert.NotEmpty(refreshHashes);
            Assert.DoesNotContain(reservationToken, text, StringComparison.Ordinal);
            foreach (var hash in refreshHashes)
            {
                Assert.DoesNotContain(Convert.ToHexString(hash), text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Convert.ToBase64String(hash), text, StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain(anaRefresh, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ana, text, StringComparison.Ordinal);
        foreach (var secret in new[] { "SecretAnaPushToken", "a11ce5ec0de7", "0bde7becf0", "Staff only marker", "fee1600d", "quarantine/ana-", "SecretOcr", "0.420", "g-ana" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        // Nothing of other fans' beyond their handles: not Ben's email, Spotify, review, report, or answer, nor Zed (who
        // follows Ben, not Ana).
        foreach (var other in new[] { "ben@example.test", "sp-user-ben", "Ben's private review", "Ben's report", "Ben's answer", "\"zed\"", "zed@" })
        {
            Assert.DoesNotContain(other, text, StringComparison.Ordinal);
        }

        // Ben was named as Ana's +1: his export says so, with Ana's handle and nothing of her standing.
        var benExport = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, "/v1/me/export", ben)).Content.ReadAsStringAsync()).RootElement;
        var guestOf = Single(benExport.GetProperty("tickets"), "guest_of");
        Assert.Equal("ana", guestOf.GetProperty("host_handle").GetString());
        Assert.False(guestOf.TryGetProperty("percentile", out _));
        Assert.Equal("sp-user-ben", benExport.GetProperty("spotify").GetProperty("connection").GetProperty("spotify_user_id").GetString());
        Assert.Equal("Ben's report", Single(benExport.GetProperty("activity"), "reports_filed").GetProperty("details").GetString());
    }

    [Fact]
    public async Task The_export_is_a_dated_download_limited_per_user_and_gone_with_the_account()
    {
        var dee = (await SignInWithRefreshAsync("dee")).Access;
        var eve = (await SignInWithRefreshAsync("eve")).Access;

        var first = await SendAsync(HttpMethod.Get, "/v1/me/export", dee);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/json", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", first.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"fandom-score-export-{DateTime.UtcNow:yyyy-MM-dd}.json", first.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("no-store", first.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        var empty = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("spotify").GetProperty("connection").ValueKind);
        Assert.Empty(empty.GetProperty("proof").GetProperty("submissions").EnumerateArray());

        for (var i = 1; i < FandomScore.Api.Features.Privacy.DataExportEndpoints.ExportsPerHour; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/v1/me/export", dee)).StatusCode);
        }

        var limited = await SendAsync(HttpMethod.Get, "/v1/me/export", dee);
        var problem = await JsonAsync(limited, HttpStatusCode.TooManyRequests);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        // Per user: Eve isn't held up by Dee.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/v1/me/export", eve)).StatusCode);

        // Signed out or deleted: no export.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/v1/me/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Delete, "/v1/me", eve)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me/export", eve)).StatusCode);
    }

    private static JsonElement Single(JsonElement area, string section) => Assert.Single(area.GetProperty(section).EnumerateArray());

    // Rows the API has no write path for yet (Spotify sync, the worker, staff) or that are quicker to write directly.
    private async Task SeedAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            WITH u AS (SELECT (SELECT id FROM users WHERE handle = 'ana') AS ana, (SELECT id FROM users WHERE handle = 'ben') AS ben,
                              (SELECT id FROM users WHERE handle = 'zed') AS zed)
            INSERT INTO follows (follower_id, followee_id)
            SELECT ana, ben FROM u UNION ALL SELECT ben, ana FROM u UNION ALL SELECT zed, ben FROM u;
            INSERT INTO notifications (user_id, kind, subject_id, data)
            SELECT id, 'staff_report', gen_random_uuid(), '{"details": "Staff only marker"}' FROM users WHERE handle = 'ana';

            INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes)
            SELECT id, 'sp-user-' || handle, '\xa11ce5ec0de7'::bytea, '{user-library-read}' FROM users WHERE handle IN ('ana', 'ben');
            UPDATE spotify_connections SET plays_cursor = 1234567 WHERE spotify_user_id = 'sp-user-ana';

            INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks) SELECT id, @_artistId, 7 FROM users WHERE handle = 'ana';
            INSERT INTO tracks (album_id, title, spotify_track_id)
            SELECT id, 'Ana Track', '0000000000000000000ana' FROM albums WHERE artist_id = @_artistId;
            INSERT INTO user_track_likes (user_id, track_id, source, saved_at)
            SELECT u.id, t.id, 'liked', now() FROM users u, tracks t WHERE u.handle = 'ana';
            INSERT INTO user_album_saves (user_id, album_id, saved_at)
            SELECT u.id, a.id, now() FROM users u, albums a WHERE u.handle = 'ana' AND a.artist_id = @_artistId;
            INSERT INTO user_plays (user_id, artist_id, spotify_track_id, played_at)
            SELECT id, @_artistId, '0000000000000000000ana', now() - interval '1 hour' FROM users WHERE handle = 'ana';

            INSERT INTO album_logs (user_id, album_id, rating, review, visibility)
            SELECT u.id, a.id, 8, CASE u.handle WHEN 'ana' THEN 'Ana''s review of Album 1' ELSE 'Ben''s private review' END,
                   CASE u.handle WHEN 'ana' THEN 'public' ELSE 'private' END::visibility
            FROM users u, albums a WHERE u.handle IN ('ana', 'ben') AND a.artist_id = @_artistId;
            INSERT INTO score_events (user_id, artist_id, rule_key, points, source_type, source_id)
            SELECT l.user_id, @_artistId, (SELECT rule_key FROM scoring_rules ORDER BY rule_key LIMIT 1), 5, 'album_log', l.id
            FROM album_logs l JOIN users u ON u.id = l.user_id WHERE u.handle = 'ana'
              AND NOT EXISTS (SELECT 1 FROM score_events e WHERE e.source_id = l.id);

            INSERT INTO prompts (day, text, source) VALUES (prompt_week_day(), 'Your first show?', 'staff');
            INSERT INTO prompt_answers (prompt_id, user_id, body)
            SELECT p.id, u.id, CASE u.handle WHEN 'ana' THEN 'Ana''s answer' ELSE 'Ben''s answer' END
            FROM prompts p, users u WHERE u.handle IN ('ana', 'ben');

            INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index, chosen_index, correct, answered_at)
            SELECT id, current_date - 1, @_artistId, 'release_year', 'When?', '{2019,2020,2021}', 1, 1, true, now() FROM users WHERE handle = 'ana';
            INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index)
            SELECT id, current_date, @_artistId, 'release_year', 'When again?', '{2019,2020,2021}', 2 FROM users WHERE handle = 'ana';

            INSERT INTO post_reactions (target_type, target_id, user_id, reaction)
            SELECT 'log', l.id, u.id, 'fire' FROM album_logs l JOIN users b ON b.id = l.user_id AND b.handle = 'ben', users u WHERE u.handle = 'ana';
            INSERT INTO reports (reporter_id, target_type, target_id, reason, details)
            SELECT u.id, 'log', l.id, 'other', CASE u.handle WHEN 'ana' THEN 'Ana''s report' ELSE 'Ben''s report' END
            FROM users u JOIN album_logs l ON l.user_id <> u.id JOIN users author ON author.id = l.user_id AND author.handle IN ('ana', 'ben')
            WHERE u.handle IN ('ana', 'ben');

            INSERT INTO submissions (user_id, artist_id, kind, status, caption, concert_date, concert_venue, concert_city,
                                     order_ref_hash, trust_score, extracted)
            SELECT id, @_artistId, 'concert_photo', 'pending', 'Ana at the Metro', current_date - 3, 'Metro', 'Chicago',
                   '\x0bde7becf0'::bytea, 0.420, '{"note": "SecretOcr"}'
            FROM users WHERE handle = 'ana';
            INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes)
            SELECT s.id, 'quarantine/ana-photo.jpg', '\xfee1600d'::bytea, 'image/jpeg', 1234 FROM submissions s;

            INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id)
            SELECT id, 1, '1111111111111111111ana', 'Ana Grab', @_artistId FROM users WHERE handle = 'ana';
            INSERT INTO push_devices (user_id, token, platform)
            SELECT id, 'ExponentPushToken[SecretAnaPushToken]', 'ios' FROM users WHERE handle = 'ana';
            """,
            new { _artistId });
    }

    private async Task<Guid> AllocationAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        var eventId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, venue, city_id, starts_at) VALUES (@_artistId, 'Geese at Metro', 'Metro', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
            VALUES (@eventId, 3, 4500, 10, now() - interval '1 hour', now() + interval '2 seconds', now()) RETURNING id
            """,
            new { eventId });
    }

    private async Task ScoreAsync(string handle, decimal score)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
            SELECT id, @_artistId, @score, @score, false, 0.1 FROM users WHERE handle = @handle
            """,
            new { _artistId, score, handle });
    }

    private async Task<(string Access, string Refresh)> SignInWithRefreshAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        var session = await JsonAsync(response, HttpStatusCode.Created);
        return (session.GetProperty("access_token").GetString()!, session.GetProperty("refresh_token").GetString()!);
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
