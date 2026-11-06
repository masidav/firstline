using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Storage;
using FandomScore.Worker.AccountRetention;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.StorageCleanup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// Account deletion and the 30-day purge leave nothing personal behind except the documented exceptions (privacy
/// README, "Data inventory"), checked by scanning every column of every table. Also the daily housekeeping retention.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountDeletionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Handle = "privacy_ana";
    private const string Marker = "PRIVMARK";   // in every piece of free text the fan writes

    /// <summary>
    /// Columns that may still name the fan after the purge, and why (privacy README, "Kept after the purge"). The users
    /// row itself is anonymous; everything else points at it by id only.
    /// </summary>
    private static readonly HashSet<string> KeptIds =
    [
        "users.id",                         // the anonymous row the kept records point at
        "score_events.user_id",             // the ledger: append-only, reversed at purge
        "allocation_registrations.user_id", // draw records (withdrawn, hubs and +1 cleared), kept so draws stay repeatable
        "reports.reporter_id",              // reports the fan filed, kept for moderation (open decision)
        "fraud_flags.user_id",              // fraud records (open decision)
        "fraud_appeals.user_id",
        "eligibility_audit.user_id",
        "eligibility_audit.actor_id",
        "jobs.payload",                     // finished jobs are pruned after 7 days (30 if failed)
        "jobs.dedupe_key",
    ];

    /// <summary>Free text the fan wrote that is kept with the records above (open decision).</summary>
    private static readonly HashSet<string> KeptText = ["reports.details", "fraud_appeals.message"];

    private static readonly string[] Chicago = ["us-il-chicago"];

    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"deletion-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"deletion_{Guid.NewGuid():N}");
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
    public async Task A_deleted_and_purged_account_leaves_nothing_personal_beyond_the_documented_exceptions()
    {
        var staff = await SignInAsync("privacy_staff");
        await ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = 'privacy_staff'");
        var ana = await SignInAsync(Handle);
        var ben = await SignInAsync("privacy_ben");
        var anaId = await ScalarAsync<Guid>("SELECT id FROM users WHERE handle = @Handle", new { Handle });

        // Through the API: hubs, a registration naming Ben as +1 (Ben gets a notice with Ana's handle).
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", ana, new { city_ids = Chicago }), HttpStatusCode.OK);
        await ExecuteAsync(
            """
            INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
            SELECT id, @_artistId, 90, 90, false, 0.1 FROM users WHERE handle = @Handle
            """,
            new { _artistId, Handle });
        var allocationId = await AllocationAsync();
        // The fraud signals (0054): her phone, her sign-up and registration address, and her eligibility checks.
        await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/push-devices", ana, new { token = "ExponentPushToken[privacyanaphone]", platform = "ios" }), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{allocationId}/eligibility", ana), HttpStatusCode.OK);
        await ExecuteAsync("SELECT record_client_address(@anaId, 'signup', NULL, '203.0.113.99'), record_client_address(@anaId, 'registration', @allocationId, '203.0.113.99')",
            new { anaId, allocationId });
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", ana, new { plus_one = true }), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration/guest", ana, new { handle = "privacy_ben" }), HttpStatusCode.OK);
        await SeedAsync(anaId);
        Assert.Contains(await NoticesAsync(staff), n => n.Body.StartsWith($"@{Handle} sent", StringComparison.Ordinal));
        Assert.Contains(await NoticesAsync(ben), n => n.Body.StartsWith($"@{Handle} named you", StringComparison.Ordinal));
        // The scan below finds her before deletion: in her row, her listening, her words, and others' notices.
        var before = (await ColumnsContainingAsync(anaId.ToString(), uuidValue: anaId))
            .Concat(await ColumnsContainingAsync(Marker)).Concat(await ColumnsContainingAsync(Handle)).ToHashSet();
        foreach (var column in new[] { "users.handle", "users.email", "notifications.data", "user_plays.user_id", "album_logs.review", "submissions.extracted", "idempotency_keys.response",
                                       "client_address_events.user_id", "push_device_accounts.user_id", "spotify_account_links.user_id", "eligibility_views.user_id" })
        {
            Assert.Contains(column, before);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Delete, "/v1/me", ana)).StatusCode);

        // At once: listening data, scores, cached responses, +1 declines, the picture, and her handle in others' notices.
        foreach (var table in new[] { "user_plays", "user_album_saves", "user_track_likes", "user_artist_signals", "spotify_connections", "music_connections",
                                      "fan_overall_scores", "idempotency_keys", "push_devices", "follows", "post_reactions", "notifications",
                                      "client_address_events", "push_device_accounts", "spotify_account_links", "eligibility_views", "hub_location_checks" })
        {
            var column = table == "follows" ? "follower_id" : "user_id";
            Assert.True(await ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE {column} = @anaId", new { anaId }) == 0, table);
        }

        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM plus_one_declines WHERE guest_user_id = @anaId OR host_user_id = @anaId", new { anaId }));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM uploads WHERE user_id = @anaId AND purpose = 'avatar'", new { anaId }));
        Assert.True(await ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM storage_deletions WHERE storage_key = 'media/avatars/privacy-ana')"));
        Assert.Contains(await NoticesAsync(staff), n => n.Body == "A deleted account sent concert photo for Geese.");
        Assert.Contains(await NoticesAsync(staff), n => n.Body == "A deleted account appealed their account review.");
        Assert.Contains(await NoticesAsync(ben), n => n.Body.StartsWith("A fan who has since deleted their account named you", StringComparison.Ordinal));

        // 30 days later: storage cleanup removes her files, then the purge her content.
        await ExecuteAsync("UPDATE users SET deleted_at = deleted_at - interval '31 days' WHERE id = @anaId", new { anaId });
        await new StorageCleanupJob(_db, new LocalFileStore(_storage), NullLogger<StorageCleanupJob>.Instance)
            .RunAsync(new ClaimedJob(1, StorageCleanupJob.JobKind, "{}", 1, 10), CancellationToken.None);
        await new PurgeDeletedAccountsJob(_db, NullLogger<PurgeDeletedAccountsJob>.Instance)
            .RunAsync(new ClaimedJob(2, "accounts.purge_deleted", "{}", 1, 10), CancellationToken.None);
        Assert.True(await ScalarAsync<bool>("SELECT content_purged_at IS NOT NULL FROM users WHERE id = @anaId", new { anaId }));

        // Every column of every table: her id, handle, email, and words appear only where documented.
        var idHits = await ColumnsContainingAsync(anaId.ToString(), uuidValue: anaId);
        var textHits = await ColumnsContainingAsync(Marker);
        var handleHits = await ColumnsContainingAsync(Handle);
        Assert.Empty(idHits.Except(KeptIds));
        Assert.Empty(textHits.Except(KeptText));
        Assert.Empty(handleHits);
        Assert.Equal(KeptText, textHits);
        foreach (var kept in new[] { "users.id", "score_events.user_id", "allocation_registrations.user_id", "reports.reporter_id", "fraud_flags.user_id" })
        {
            Assert.Contains(kept, idHits);
        }

        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM score_events WHERE user_id = @anaId AND reversed_at IS NULL", new { anaId }));
        Assert.True(await ScalarAsync<bool>(
            "SELECT withdrawn_reason = 'account_deleted' AND hub_city_ids = '{}' AND guest_user_id IS NULL FROM allocation_registrations WHERE user_id = @anaId",
            new { anaId }));
    }

    [Fact]
    public async Task Housekeeping_removes_old_sessions_cached_responses_and_push_records_only()
    {
        var job = new PruneExpiredRecordsJob(_db, NullLogger<PruneExpiredRecordsJob>.Instance);
        Assert.Equal("privacy.retention", job.Kind);
        Assert.Equal(TimeSpan.FromDays(1), job.Every);
        await SignInAsync("keeper");
        await ExecuteAsync(
            """
            INSERT INTO users (handle, email) VALUES ('old_fan', 'old_fan@example.test');
            WITH u AS (SELECT id FROM users WHERE handle = 'old_fan')
            INSERT INTO auth_sessions (user_id, created_at, last_refreshed_at, expires_at, revoked_at, revoked_reason)
            SELECT id, now() - interval '200 days', now() - interval '200 days', now() - interval '31 days', NULL, NULL FROM u UNION ALL
            SELECT id, now() - interval '100 days', now() - interval '100 days', now() + interval '10 days', now() - interval '31 days', 'sign_out' FROM u UNION ALL
            SELECT id, now() - interval '100 days', now() - interval '100 days', now() - interval '29 days', NULL, NULL FROM u UNION ALL
            SELECT id, now() - interval '10 days', now() - interval '10 days', now() + interval '10 days', now() - interval '29 days', 'sign_out' FROM u;
            INSERT INTO refresh_tokens (token_hash, session_id, issued_at, expires_at)
            SELECT sha256(s.id::text::bytea), s.id, s.created_at, s.expires_at FROM auth_sessions s JOIN users u ON u.id = s.user_id WHERE u.handle = 'old_fan';
            INSERT INTO idempotency_keys (user_id, key, request_sha256, status_code, response, created_at)
            SELECT id, k, sha256(k::bytea), 201, '{}', now() - age FROM users, (VALUES ('old', interval '25 hours'), ('new', interval '1 hour')) v(k, age)
            WHERE handle = 'old_fan';
            INSERT INTO push_devices (user_id, token, platform, disabled_at, disabled_reason)
            SELECT id, 'ExponentPushToken[' || t || ']', 'ios', now() - age, CASE WHEN age IS NOT NULL THEN 'device_not_registered' END
            FROM users, (VALUES ('gonedevice1', interval '31 days'), ('recentdevice1', interval '1 day'), ('activedevice1', NULL)) v(t, age) WHERE handle = 'old_fan';
            INSERT INTO notifications (user_id, kind, subject_id, data) SELECT id, 'presale', gen_random_uuid(), '{}' FROM users WHERE handle = 'old_fan';
            -- Removed: one sent 31 days ago, and one to the device that goes. Kept: one sent yesterday.
            INSERT INTO push_deliveries (notification_id, device_id, sent_at, status)
            SELECT n.id, d.id, now() - CASE d.token WHEN 'ExponentPushToken[activedevice1]' THEN interval '31 days' ELSE interval '1 day' END, 'sent'
            FROM notifications n, push_devices d WHERE n.kind = 'presale';
            """);
        var sessionsBefore = await ScalarAsync<long>("SELECT count(*) FROM auth_sessions");

        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);
        await job.RunAsync(new ClaimedJob(2, job.Kind, "{}", 1, 10), CancellationToken.None);   // safe to repeat

        Assert.Equal(sessionsBefore - 2, await ScalarAsync<long>("SELECT count(*) FROM auth_sessions"));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM auth_sessions WHERE expires_at < now() - interval '30 days' OR revoked_at < now() - interval '30 days'"));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM refresh_tokens t WHERE NOT EXISTS (SELECT 1 FROM auth_sessions s WHERE s.id = t.session_id)"));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM auth_sessions s JOIN users u ON u.id = s.user_id WHERE u.handle = 'keeper'"));
        Assert.Equal(["new"], (await QueryAsync("SELECT key FROM idempotency_keys ORDER BY key")).ToArray());
        Assert.Equal(["ExponentPushToken[activedevice1]", "ExponentPushToken[recentdevice1]"],
            (await QueryAsync("SELECT token FROM push_devices ORDER BY token")).ToArray());
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM push_deliveries"));
    }

    // Rows with no API write path here, or quicker to write directly. Every piece of free text carries the marker.
    private async Task SeedAsync(Guid anaId)
    {
        await ExecuteAsync(
            """
            WITH u AS (SELECT @anaId AS ana, (SELECT id FROM users WHERE handle = 'privacy_ben') AS ben,
                              (SELECT id FROM users WHERE handle = 'privacy_staff') AS staff)
            INSERT INTO follows (follower_id, followee_id) SELECT ana, ben FROM u UNION ALL SELECT ben, ana FROM u;
            UPDATE users SET display_name = 'Ana PRIVMARK' WHERE id = @anaId;

            INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes) VALUES (@anaId, 'sp-privmark', '\x01', '{}');
            INSERT INTO music_connections (user_id, provider, provider_user_id, token_enc) VALUES (@anaId, 'tidal', 'td-privmark', '\x01');
            INSERT INTO user_artist_signals (user_id, artist_id, liked_tracks) VALUES (@anaId, @_artistId, 3);
            INSERT INTO user_artist_signals (user_id, provider, artist_id, liked_tracks) VALUES (@anaId, 'tidal', @_artistId, 1);
            INSERT INTO tracks (album_id, title, spotify_track_id) SELECT id, 'Track', '0000000000000000000ana' FROM albums WHERE artist_id = @_artistId;
            INSERT INTO user_track_likes (user_id, track_id, source, saved_at) SELECT @anaId, id, 'liked', now() FROM tracks;
            INSERT INTO user_album_saves (user_id, album_id, saved_at) SELECT @anaId, id, now() FROM albums WHERE artist_id = @_artistId;
            INSERT INTO user_plays (user_id, artist_id, spotify_track_id, played_at) VALUES (@anaId, @_artistId, '0000000000000000000ana', now());

            INSERT INTO album_logs (user_id, album_id, rating, review, visibility)
            SELECT u.id, a.id, 8, CASE WHEN u.id = @anaId THEN 'PRIVMARK review' ELSE 'Ben review' END, 'public'
            FROM users u, albums a WHERE u.handle IN ('privacy_ana', 'privacy_ben') AND a.artist_id = @_artistId;
            INSERT INTO score_events (user_id, artist_id, rule_key, points, source_type, source_id)
            SELECT @anaId, @_artistId, (SELECT rule_key FROM scoring_rules ORDER BY rule_key LIMIT 1), 5, 'album_log', l.id
            FROM album_logs l WHERE l.user_id = @anaId AND NOT EXISTS (SELECT 1 FROM score_events e WHERE e.source_id = l.id);
            INSERT INTO comments (album_log_id, user_id, body)
            SELECT l.id, @anaId, 'PRIVMARK comment' FROM album_logs l WHERE l.user_id <> @anaId;
            INSERT INTO lists (user_id, title, description) VALUES (@anaId, 'PRIVMARK list', 'PRIVMARK');
            INSERT INTO prompts (day, text, source) VALUES (prompt_week_day(), 'Your first show?', 'staff');
            INSERT INTO prompt_answers (prompt_id, user_id, body) SELECT id, @anaId, 'PRIVMARK answer' FROM prompts;
            INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index, chosen_index, correct, answered_at)
            VALUES (@anaId, current_date - 1, @_artistId, 'release_year', 'When?', '{2019,2020,2021}', 1, 1, true, now());
            INSERT INTO post_reactions (target_type, target_id, user_id, reaction)
            SELECT 'log', l.id, @anaId, 'fire' FROM album_logs l WHERE l.user_id <> @anaId;
            INSERT INTO reports (reporter_id, target_type, target_id, reason, details)
            SELECT @anaId, 'log', l.id, 'other', 'PRIVMARK report' FROM album_logs l WHERE l.user_id <> @anaId UNION ALL
            SELECT l2.user_id, 'log', l.id, 'other', 'About Ana''s log' FROM album_logs l, album_logs l2 WHERE l.user_id = @anaId AND l2.user_id <> @anaId;
            INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id) VALUES (@anaId, 1, '1111111111111111111ana', 'PRIVMARK song', @_artistId);
            INSERT INTO outbound_clicks (user_id, artist_id, kind) VALUES (@anaId, @_artistId, 'ticket');
            INSERT INTO plus_one_declines (event_id, guest_user_id, host_user_id)
            SELECT event_id, @anaId, (SELECT id FROM users WHERE handle = 'privacy_ben') FROM allocations;
            INSERT INTO idempotency_keys (user_id, key, request_sha256, status_code, response)
            VALUES (@anaId, 'k1', sha256('k1'), 201, '{"review": "PRIVMARK cached"}');

            -- Proof waiting for review: staff get a notice with her handle.
            INSERT INTO submissions (user_id, artist_id, kind, status, caption, extracted, order_ref_hash, concert_date, concert_venue, concert_city)
            VALUES (@anaId, @_artistId, 'concert_photo', 'needs_review', 'PRIVMARK caption', '{"ocr": "PRIVMARK ocr"}', '\x02',
                    current_date - 3, 'PRIVMARK venue', 'Chicago');
            INSERT INTO submission_files (submission_id, storage_key, sha256, mime_type, bytes)
            SELECT id, 'quarantine/privacy-ana.jpg', '\x03', 'image/jpeg', 1 FROM submissions WHERE user_id = @anaId;
            INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes, storage_key, sha256, bytes, uploaded_at, submission_id)
            SELECT @anaId, 'submission', 'image/jpeg', 1, 'quarantine/privacy-ana.jpg', sha256('x3'), 1, now(), id FROM submissions WHERE user_id = @anaId;
            -- A profile picture.
            WITH pic AS (
              INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes, storage_key, sha256, bytes, uploaded_at)
              VALUES (@anaId, 'avatar', 'image/jpeg', 1, 'media/avatars/privacy-ana', sha256('x4'), 1, now()) RETURNING id)
            UPDATE users SET avatar_upload_id = pic.id, avatar_url = 'https://api.test/v1/avatars/' || pic.id FROM pic WHERE users.id = @anaId;

            -- A fraud flag and her appeal: staff get a notice with her handle.
            INSERT INTO fraud_flags (user_id, reason, source, raised_by, note)
            SELECT @anaId, 'other', 'staff', id, 'Staff note' FROM users WHERE handle = 'privacy_staff';
            INSERT INTO fraud_appeals (flag_id, user_id, message) SELECT id, @anaId, 'PRIVMARK appeal' FROM fraud_flags;

            INSERT INTO push_devices (user_id, token, platform) VALUES (@anaId, 'ExponentPushToken[privmarkdevice]', 'ios');
            INSERT INTO push_deliveries (notification_id, device_id, status)
            SELECT n.id, d.id, 'sent' FROM notifications n, push_devices d WHERE n.user_id = @anaId AND d.user_id = @anaId;
            SELECT refresh_fan_overall_scores();
            """,
            new { anaId, _artistId });
        await new LocalFileStore(_storage).PutAsync("quarantine/privacy-ana.jpg", new byte[] { 1 }, CancellationToken.None);
        await new LocalFileStore(_storage).PutAsync("media/avatars/privacy-ana", new byte[] { 1 }, CancellationToken.None);
    }

    /// <summary>"table.column" for every column in which some row contains <paramref name="text"/> (or equals the uuid).</summary>
    private async Task<HashSet<string>> ColumnsContainingAsync(string text, Guid? uuidValue = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        var columns = await connection.QueryAsync<(string Table, string Column, string Type)>(
            """
            SELECT c.table_name, c.column_name, c.udt_name
            FROM information_schema.columns c JOIN information_schema.tables t USING (table_schema, table_name)
            WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE' AND c.table_name <> 'schemaversions'
              AND c.udt_name IN ('uuid', '_uuid', 'text', '_text', 'citext', 'varchar', 'jsonb', 'json')
            """);
        var hits = new HashSet<string>();
        foreach (var (table, column, type) in columns)
        {
            var condition = type switch
            {
                "uuid" when uuidValue is not null => $"\"{column}\" = @uuidValue",
                "_uuid" when uuidValue is not null => $"@uuidValue = ANY(\"{column}\")",
                "uuid" or "_uuid" => null,
                _ => $"strpos(\"{column}\"::text, @text) > 0",
            };
            if (condition is not null
                && await connection.ExecuteScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM \"{table}\" WHERE {condition})", new { text, uuidValue }))
            {
                hits.Add($"{table}.{column}");
            }
        }

        return hits;
    }

    private async Task<List<(string Kind, string Body)>> NoticesAsync(string token)
    {
        var page = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", token), HttpStatusCode.OK);
        return page.GetProperty("items").EnumerateArray()
            .Select(n => (n.GetProperty("kind").GetString()!, n.GetProperty("body").GetString()!)).ToList();
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
            VALUES (@eventId, 3, 4500, 10, now() - interval '1 hour', now() + interval '1 day', now()) RETURNING id
            """,
            new { eventId });
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

    private async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    private async Task<IEnumerable<string>> QueryAsync(string sql)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QueryAsync<string>(sql);
    }
}
