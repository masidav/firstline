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
using Microsoft.Extensions.Logging.Abstractions;
using FandomScore.Spotify;
using FandomScore.Worker.Fraud;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.SpotifySync;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>
/// Fraud review, appeals, and the eligibility audit (migration 0042): a flag holds a fan out of registering, the draw,
/// and returned seats until it's cleared; the fan appeals once; every decision is in the audit log. Automatic
/// detection (0043, 0045): an open rule flag only blocks registering until staff uphold it, and the fan doesn't know about
/// it until then (0044).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FraudReviewApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;
    private Guid _eventId;
    private string _staff = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"fraud_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();

        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-fraud", "Wednesday", releases: 1);
        await using var connection = await _db.OpenConnectionAsync();
        _eventId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO events (artist_id, name, city_id, starts_at) VALUES (@_artistId, 'Wednesday', 'us-il-chicago', now() + interval '20 days') RETURNING id",
            new { _artistId });
        _staff = await FanAsync("fraud_staff", score: null);
        await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = 'fraud_staff'");
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task A_flagged_fan_cannot_register_and_each_refusal_is_audited_once()
    {
        var allocationId = await AllocationAsync(tickets: 5);
        var fan = await FanAsync("farm_fan", score: 95, "us-il-chicago");

        var flag = await RaiseAsync("farm_fan", "streaming_farm", note: "Same 3 tracks on loop, 22 hours a day",
            evidence: new { looped_tracks = 3, hours_a_day = 22 });
        Assert.Equal(("open", "streaming_farm", "fraud_staff"), (flag.GetProperty("status").GetString(),
            flag.GetProperty("reason").GetString(), flag.GetProperty("raised_by").GetString()));
        Assert.Equal(22, flag.GetProperty("evidence").GetProperty("hours_a_day").GetInt32());
        Assert.True(flag.GetProperty("context").GetProperty("trust_parts").TryGetProperty("account_age", out _));

        var eligibility = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{allocationId}/eligibility", fan), HttpStatusCode.OK);
        Assert.Equal("under_review", eligibility.GetProperty("reason").GetString());
        for (var i = 0; i < 2; i++)
        {
            var problem = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", fan), HttpStatusCode.Forbidden);
            Assert.Equal("under_review", problem.GetProperty("code").GetString());
        }

        // The fan sees the reason and status, not the evidence or the staff note, and was told.
        var review = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/fraud-review", fan), HttpStatusCode.OK);
        Assert.True(review.GetProperty("on_hold").GetBoolean());
        var mine = Assert.Single(review.GetProperty("flags").EnumerateArray());
        Assert.Equal(("streaming_farm", "open", true), (mine.GetProperty("reason").GetString(), mine.GetProperty("status").GetString(),
            mine.GetProperty("can_appeal").GetBoolean()));
        Assert.False(mine.TryGetProperty("evidence", out _));
        Assert.False(mine.TryGetProperty("note", out _));
        var notices = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", fan), HttpStatusCode.OK);
        var notice = Assert.Single(notices.GetProperty("items").EnumerateArray());
        Assert.Equal(("fraud_hold", "/account-review"), (notice.GetProperty("kind").GetString(), notice.GetProperty("link").GetString()));

        // Raised, then one refusal (the retry within the hour isn't logged again), with the flag and score it was based on.
        var audit = await AuditAsync("?handle=farm_fan");
        Assert.Equal(["registration_blocked", "flag_raised"], audit.Select(e => e.GetProperty("action").GetString()));
        var blocked = audit[0];
        Assert.Equal(("under_review", "farm_fan"), (blocked.GetProperty("reason").GetString(), blocked.GetProperty("actor").GetString()));
        Assert.Equal(95m, blocked.GetProperty("inputs").GetProperty("raw_score").GetDecimal());
        Assert.Equal(flag.GetProperty("id").GetString(), blocked.GetProperty("flag_id").GetString());
        Assert.Equal("fraud_staff", audit[1].GetProperty("actor").GetString());

        // Only staff use the queue and the log.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/v1/admin/fraud-flags", fan)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/v1/admin/eligibility-audit?handle=farm_fan", fan)).StatusCode);
    }

    [Fact]
    public async Task The_draw_withdraws_a_flagged_fans_registration_and_the_seat_goes_to_the_next_fan()
    {
        var allocationId = await AllocationAsync(tickets: 1);
        var cheat = await FanAsync("top_cheat", score: 99, "us-il-chicago");
        var honest = await FanAsync("honest_fan", score: 80, "us-il-chicago");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", cheat), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", honest), HttpStatusCode.Created);

        // Flagged after registering: the registration stays live until the draw.
        await RaiseAsync("top_cheat", "bot");
        var before = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{allocationId}/eligibility", cheat), HttpStatusCode.OK);
        Assert.Equal("registered", before.GetProperty("registration").GetProperty("status").GetString());

        await DrawAsync(allocationId);

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/allocations/{allocationId}/registrations", _staff), HttpStatusCode.OK);
        var statuses = list.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("handle").GetString()!, i => i.GetProperty("registration").GetProperty("status").GetString());
        Assert.Equal("selected", statuses["honest_fan"]);
        await using var connection = await _db.OpenConnectionAsync();
        Assert.Equal("fraud_hold", await connection.ExecuteScalarAsync<string>(
            "SELECT withdrawn_reason FROM allocation_registrations r JOIN users u ON u.id = r.user_id WHERE u.handle = 'top_cheat'"));

        var audit = await AuditAsync($"?allocation_id={allocationId}");
        Assert.Equal(["draw_held_out", "registered", "registered"], audit.Select(e => e.GetProperty("action").GetString()));
        Assert.Equal(("top_cheat", "bot"), (audit[0].GetProperty("handle").GetString(), audit[0].GetProperty("reason").GetString()));
        Assert.Equal(JsonValueKind.Null, audit[0].GetProperty("actor").ValueKind);   // the system
        var registered = audit.Single(e => e.GetProperty("handle").GetString() == "honest_fan");
        Assert.Equal((80m, 0m, 1), (registered.GetProperty("inputs").GetProperty("raw_score").GetDecimal(),
            registered.GetProperty("inputs").GetProperty("min_raw_score").GetDecimal(), registered.GetProperty("inputs").GetProperty("hub_match").GetInt32()));
    }

    [Fact]
    public async Task A_returned_seat_skips_a_flagged_fan()
    {
        var allocationId = await AllocationAsync(tickets: 1);
        var winner = await FanAsync("seat_winner", score: 99, "us-il-chicago");
        await FanAsync("next_flagged", score: 90, "us-il-chicago");
        var after = await FanAsync("after_that", score: 80, "us-il-chicago");
        foreach (var token in new[] { winner, after })
        {
            await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", token), HttpStatusCode.Created);
        }

        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", await TokenAsync("next_flagged")),
            HttpStatusCode.Created);
        await DrawAsync(allocationId);
        await RaiseAsync("next_flagged", "duplicate_account");

        var reservations = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/reservations", winner), HttpStatusCode.OK);
        var reservationId = reservations.GetProperty("items")[0].GetProperty("id").GetString();
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/reservations/{reservationId}/return", winner), HttpStatusCode.OK);

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", after), HttpStatusCode.OK);
        Assert.Equal("selected", mine.GetProperty("items")[0].GetProperty("registration").GetProperty("status").GetString());
        var audit = await AuditAsync("?handle=next_flagged");
        Assert.Equal("backfill_skipped", audit[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_fan_appeals_once_and_a_granted_appeal_lets_them_register_again()
    {
        var allocationId = await AllocationAsync(tickets: 5);
        var fan = await FanAsync("appealing_fan", score: 90, "us-il-chicago");
        var flagId = (await RaiseAsync("appealing_fan", "shared_device")).GetProperty("id").GetString();

        var empty = await SendAsync(HttpMethod.Post, $"/v1/me/fraud-review/flags/{flagId}/appeal", fan, new { message = "  " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        var someoneElse = await FanAsync("bystander", score: null);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/v1/me/fraud-review/flags/{flagId}/appeal", someoneElse,
            new { message = "Not mine" })).StatusCode);

        var appeal = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/fraud-review/flags/{flagId}/appeal", fan,
            new { message = "My roommate and I share a laptop." }), HttpStatusCode.Created);
        Assert.Equal("pending", appeal.GetProperty("status").GetString());
        var twice = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/fraud-review/flags/{flagId}/appeal", fan,
            new { message = "Again" }), HttpStatusCode.Conflict);
        Assert.Equal("appeal_unavailable", twice.GetProperty("code").GetString());

        // Staff hear about it, and the flag is in the queue with its appeal.
        var staffNotices = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", _staff), HttpStatusCode.OK);
        Assert.Contains(staffNotices.GetProperty("items").EnumerateArray(), n => n.GetProperty("kind").GetString() == "staff_appeal");
        var queue = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/fraud-flags", _staff), HttpStatusCode.OK);
        var queued = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal("My roommate and I share a laptop.", queued.GetProperty("appeal").GetProperty("message").GetString());

        var appealId = appeal.GetProperty("id").GetString();
        var decided = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/fraud-appeals/{appealId}/decision", _staff,
            new { decision = "grant", response = "Thanks, that checks out." }), HttpStatusCode.OK);
        Assert.Equal(("cleared", "granted", "fraud_staff"), (decided.GetProperty("status").GetString(),
            decided.GetProperty("appeal").GetProperty("status").GetString(), decided.GetProperty("appeal_decided_by").GetString()));
        Assert.Equal("appeal_unavailable", (await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/fraud-appeals/{appealId}/decision",
            _staff, new { decision = "deny" }), HttpStatusCode.Conflict)).GetProperty("code").GetString());

        var review = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/fraud-review", fan), HttpStatusCode.OK);
        Assert.False(review.GetProperty("on_hold").GetBoolean());
        var flag = review.GetProperty("flags")[0];
        Assert.Equal(("cleared", false, "Thanks, that checks out."), (flag.GetProperty("status").GetString(),
            flag.GetProperty("can_appeal").GetBoolean(), flag.GetProperty("appeal").GetProperty("response").GetString()));
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", fan), HttpStatusCode.Created);

        // One notice for the decision (no separate "cleared" notice), and the queue is empty.
        var notices = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", fan), HttpStatusCode.OK);
        Assert.Equal(["appeal_decided", "fraud_hold"], notices.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("kind").GetString()));
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/fraud-flags", _staff), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray());

        var audit = await AuditAsync("?handle=appealing_fan");
        Assert.Equal(["registered", "flag_cleared", "appeal_granted", "appeal_filed", "flag_raised"],
            audit.Select(e => e.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task A_denied_appeal_upholds_the_flag_and_staff_decisions_follow_the_rules()
    {
        await FanAsync("denied_fan", score: 90, "us-il-chicago");
        var fan = await TokenAsync("denied_fan");
        var flagId = (await RaiseAsync("denied_fan", "other")).GetProperty("id").GetString();

        // One open flag per fan; unknown handles and bad input are refused.
        Assert.Equal("flag_open", (await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/fraud-flags", _staff,
            new { handle = "denied_fan", reason = "bot" }), HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/v1/admin/fraud-flags", _staff,
            new { handle = "nobody_here", reason = "bot" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Post, "/v1/admin/fraud-flags", _staff,
            new { handle = "denied_fan", reason = "vibes" })).StatusCode);

        var appeal = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/fraud-review/flags/{flagId}/appeal", fan,
            new { message = "I just really like this band." }), HttpStatusCode.Created);
        var denied = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/fraud-appeals/{appeal.GetProperty("id").GetString()}/decision",
            _staff, new { decision = "deny", response = "The pattern matches a farm." }), HttpStatusCode.OK);
        Assert.Equal(("upheld", "denied"), (denied.GetProperty("status").GetString(), denied.GetProperty("appeal").GetProperty("status").GetString()));
        var review = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/fraud-review", fan), HttpStatusCode.OK);
        Assert.True(review.GetProperty("on_hold").GetBoolean());
        Assert.False(review.GetProperty("flags")[0].GetProperty("can_appeal").GetBoolean());

        var upheld = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/fraud-flags?status=upheld", _staff), HttpStatusCode.OK);
        Assert.Single(upheld.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"/v1/admin/fraud-flags/{flagId}/decision", _staff,
            new { decision = "uphold" })).StatusCode);

        // Staff can still clear an upheld flag; a cleared flag is final.
        var cleared = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/fraud-flags/{flagId}/decision", _staff,
            new { decision = "clear", note = "Second look: real fan." }), HttpStatusCode.OK);
        Assert.Equal(("cleared", "fraud_staff"), (cleared.GetProperty("status").GetString(), cleared.GetProperty("decided_by").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"/v1/admin/fraud-flags/{flagId}/decision", _staff,
            new { decision = "uphold" })).StatusCode);

        // A new flag can be raised after a cleared one, and decided directly.
        var second = (await RaiseAsync("denied_fan", "bot")).GetProperty("id").GetString();
        var secondUpheld = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/fraud-flags/{second}/decision", _staff,
            new { decision = "uphold", note = "Confirmed." }), HttpStatusCode.OK);
        Assert.Equal("upheld", secondUpheld.GetProperty("status").GetString());

        // The fan's download has their reviews but not the staff notes.
        var export = await SendAsync(HttpMethod.Get, "/v1/me/export", fan);
        var text = await export.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("I just really like this band.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Second look", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirmed.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_detection_job_flags_a_bot_and_its_open_flag_only_blocks_registering()
    {
        var allocationId = await AllocationAsync(tickets: 1);
        var bot = await FanAsync("rapid_bot", score: 99, "us-il-chicago");
        var other = await FanAsync("steady_fan", score: 50, "us-il-chicago");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", bot), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", other), HttpStatusCode.Created);
        var second = await AllocationAsync(tickets: 1);

        // 150 plays 35 seconds apart today: rapid_plays. A steady listener is left alone.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_plays (user_id, artist_id, spotify_track_id, played_at)
                SELECT u.id, @_artistId, 'trk-' || (i % 9), now() - interval '3 hours' + make_interval(secs => 35 * i)
                  FROM users u, generate_series(1, 150) i WHERE u.handle = 'rapid_bot';
                INSERT INTO user_plays (user_id, artist_id, spotify_track_id, played_at)
                SELECT u.id, @_artistId, 'trk-' || (i % 9), now() - interval '20 hours' + make_interval(mins => 4 * i)
                  FROM users u, generate_series(1, 40) i WHERE u.handle = 'steady_fan';
                """,
                new { _artistId });
        }

        var job = new DetectFraudJob(_db, NullLogger<DetectFraudJob>.Instance);
        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);
        await job.RunAsync(new ClaimedJob(2, job.Kind, "{}", 1, 10), CancellationToken.None);   // idempotent

        var queue = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/fraud-flags", _staff), HttpStatusCode.OK);
        var flag = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal(("rapid_bot", "rule", "bot", JsonValueKind.Null), (flag.GetProperty("user").GetProperty("handle").GetString(),
            flag.GetProperty("source").GetString(), flag.GetProperty("reason").GetString(), flag.GetProperty("raised_by").ValueKind));
        Assert.Equal(150, flag.GetProperty("evidence").GetProperty("rules").GetProperty("rapid_plays").GetProperty("plays").GetInt32());

        // Staff hear about it before the draw; the fan learns nothing until a person reviews it.
        Assert.Equal("rapid_bot", flag.GetProperty("user").GetProperty("handle").GetString());
        Assert.Equal(JsonValueKind.Null, flag.GetProperty("shown_to_fan_at").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, flag.GetProperty("context").GetProperty("next_draw_closes_at").ValueKind);
        var staffNotices = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", _staff), HttpStatusCode.OK);
        Assert.Contains(staffNotices.GetProperty("items").EnumerateArray(), n => n.GetProperty("kind").GetString() == "staff_flag_before_draw");
        var review = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/fraud-review", bot), HttpStatusCode.OK);
        Assert.False(review.GetProperty("on_hold").GetBoolean());
        Assert.Empty(review.GetProperty("flags").EnumerateArray());
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", bot), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post,
            $"/v1/me/fraud-review/flags/{flag.GetProperty("id").GetString()}/appeal", bot, new { message = "?" })).StatusCode);

        // Open rule flag: no new registrations, with a neutral reason...
        var eligibility = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{second}/eligibility", bot), HttpStatusCode.OK);
        Assert.Equal("unavailable", eligibility.GetProperty("reason").GetString());
        var blocked = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{second}/registration", bot), HttpStatusCode.Forbidden);
        Assert.Equal("registration_unavailable", blocked.GetProperty("code").GetString());
        var export = await (await SendAsync(HttpMethod.Get, "/v1/me/export", bot)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(flag.GetProperty("id").GetString()!, export, StringComparison.Ordinal);
        Assert.DoesNotContain("flag_raised", export, StringComparison.Ordinal);
        // ...but the registration made before it stays in the draw (here it wins the one seat).
        await DrawAsync(allocationId);
        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", bot), HttpStatusCode.OK);
        Assert.Equal("selected", mine.GetProperty("items")[0].GetProperty("registration").GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_detection_job_flags_a_fan_who_likes_the_whole_catalog_in_one_sitting_but_not_a_gradual_one()
    {
        var clicker = await FanAsync("click_everything", score: 80, "us-il-chicago");
        await FanAsync("gradual_saver", score: 80, "us-il-chicago");

        // A 45-track album. The clicker, who signed up just now, likes every track and saves the album in eight
        // minutes; the gradual fan liked the same tracks one every five days over the last seven months.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tracks (album_id, title, spotify_track_id)
                SELECT al.id, 'Song ' || i, 'burst-' || i FROM albums al, generate_series(1, 45) i WHERE al.artist_id = @_artistId;
                INSERT INTO user_track_likes (user_id, track_id, source, saved_at)
                SELECT u.id, t.id, 'liked', now() - interval '1 hour' + make_interval(secs => 10 * row_number() OVER (ORDER BY t.spotify_track_id))
                  FROM users u, tracks t JOIN albums al ON al.id = t.album_id WHERE u.handle = 'click_everything' AND al.artist_id = @_artistId;
                INSERT INTO user_album_saves (user_id, album_id, saved_at)
                SELECT u.id, al.id, now() - interval '50 minutes' FROM users u, albums al WHERE u.handle = 'click_everything' AND al.artist_id = @_artistId;
                INSERT INTO user_track_likes (user_id, track_id, source, saved_at)
                SELECT u.id, t.id, 'liked', now() - make_interval(days => (5 * row_number() OVER (ORDER BY t.spotify_track_id))::int)
                  FROM users u, tracks t JOIN albums al ON al.id = t.album_id WHERE u.handle = 'gradual_saver' AND al.artist_id = @_artistId;
                """,
                new { _artistId });
        }

        var job = new DetectFraudJob(_db, NullLogger<DetectFraudJob>.Instance);
        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);
        await job.RunAsync(new ClaimedJob(2, job.Kind, "{}", 1, 10), CancellationToken.None);   // idempotent

        var queue = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/fraud-flags", _staff), HttpStatusCode.OK);
        var flag = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal(("click_everything", "rule", "other", JsonValueKind.Null), (flag.GetProperty("user").GetProperty("handle").GetString(),
            flag.GetProperty("source").GetString(), flag.GetProperty("reason").GetString(), flag.GetProperty("shown_to_fan_at").ValueKind));
        var rules = flag.GetProperty("evidence").GetProperty("rules");
        var burst = Assert.Single(Assert.Single(rules.EnumerateObject(), r => r.Name == "burst_engagement").Value.EnumerateArray());
        Assert.Equal((46, 46, true, false), (burst.GetProperty("saves").GetInt32(), burst.GetProperty("all_saves").GetInt32(),
            burst.GetProperty("after_signup").GetBoolean(), burst.GetProperty("fan_tickets").GetBoolean()));

        // Hidden from the fan until a person reviews it, as for any rule flag.
        var review = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/fraud-review", clicker), HttpStatusCode.OK);
        Assert.False(review.GetProperty("on_hold").GetBoolean());
        Assert.Empty(review.GetProperty("flags").EnumerateArray());
        var allocationId = await AllocationAsync(tickets: 1);
        Assert.Equal("unavailable", (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/allocations/{allocationId}/eligibility", clicker),
            HttpStatusCode.OK)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_detection_job_flags_a_fan_who_playlists_the_whole_catalog_and_follows_in_one_sitting()
    {
        await FanAsync("playlist_everything", score: 80, "us-il-chicago");

        // Nothing liked, no album saved: 45 tracks added to a playlist in eight minutes (Spotify's added_at), then the
        // follow, which the sync dates when it first sees it (0048).
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tracks (album_id, title, spotify_track_id)
                SELECT al.id, 'Song ' || i, 'pl-burst-' || i FROM albums al, generate_series(1, 45) i WHERE al.artist_id = @_artistId;
                INSERT INTO user_track_likes (user_id, track_id, source, saved_at)
                SELECT u.id, t.id, 'playlist', now() - interval '1 hour' + make_interval(secs => 10 * row_number() OVER (ORDER BY t.spotify_track_id))
                  FROM users u, tracks t JOIN albums al ON al.id = t.album_id WHERE u.handle = 'playlist_everything' AND al.artist_id = @_artistId;
                INSERT INTO user_artist_signals (user_id, artist_id, playlist_tracks, followed)
                SELECT id, @_artistId, 45, true FROM users WHERE handle = 'playlist_everything';
                """,
                new { _artistId });
        }

        var job = new DetectFraudJob(_db, NullLogger<DetectFraudJob>.Instance);
        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);

        var queue = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/fraud-flags", _staff), HttpStatusCode.OK);
        var flag = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal(("playlist_everything", "rule", "other"), (flag.GetProperty("user").GetProperty("handle").GetString(),
            flag.GetProperty("source").GetString(), flag.GetProperty("reason").GetString()));
        var burst = Assert.Single(Assert.Single(flag.GetProperty("evidence").GetProperty("rules").EnumerateObject(),
            r => r.Name == "burst_engagement").Value.EnumerateArray());
        Assert.Equal((46, 46, true), (burst.GetProperty("saves").GetInt32(), burst.GetProperty("all_saves").GetInt32(),
            burst.GetProperty("after_signup").GetBoolean()));
        var kinds = burst.GetProperty("kinds");
        Assert.Equal((0, 45, 0, 1), (kinds.GetProperty("liked").GetInt32(), kinds.GetProperty("playlist").GetInt32(),
            kinds.GetProperty("album").GetInt32(), kinds.GetProperty("follow").GetInt32()));
    }

    [Fact]
    public async Task A_big_old_undated_playlist_on_the_first_sync_is_no_burst_but_undated_adds_on_a_later_sync_are()
    {
        // A fan who signed up just now connects Spotify. Their library has a very old playlist Spotify leaves undated,
        // with 50 tracks of another band, and they follow that band. Stamped by one sync, that used to look like a burst
        // (0048); since 0049 it's baseline: date unknown, never a burst.
        await FanAsync("old_playlist_fan", score: 80, "us-il-chicago");
        var oldBand = await ScoringSetup.ActiveArtistAsync(_db, "sp-old-band", "Old Band", releases: 1);
        using var spotify = new FakeSpotify();
        var protector = new SpotifyTokenProtector(FakeSpotify.Options());
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tracks (album_id, title, spotify_track_id)
                SELECT al.id, 'Old ' || i, 'old-' || i FROM albums al, generate_series(1, 50) i WHERE al.artist_id = @oldBand;
                INSERT INTO tracks (album_id, title, spotify_track_id)
                SELECT al.id, 'New ' || i, 'new-' || i FROM albums al, generate_series(1, 45) i WHERE al.artist_id = @_artistId;
                """,
                new { oldBand, _artistId });
            var userId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM users WHERE handle = 'old_playlist_fan'");
            await connection.ExecuteAsync(
                """
                INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes)
                VALUES (@userId, 'sp-user-1', @token, @scopes)
                """,
                new { userId, token = protector.Protect(userId, "refresh-0"), scopes = SpotifyOptions.RequiredScopes.ToArray() });
        }

        spotify.Playlists.Add(FakeSpotify.Playlist("pl-old", "sp-user-1"));
        spotify.PlaylistItems["pl-old"] = [.. Enumerable.Range(1, 50).Select(i => FakeSpotify.PlaylistItem(FakeSpotify.Track($"old-{i}", "al-old", "sp-old-band")))];
        spotify.FollowedArtistIds.Add("sp-old-band");
        await SyncAsync(spotify, protector, "old_playlist_fan");
        await DetectAsync();
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/fraud-flags", _staff), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray());

        // The next day's sync finds a new playlist, undated too, with 45 tracks of this artist, and a new follow: that
        // appeared between two syncs, so it counts.
        spotify.Playlists.Add(FakeSpotify.Playlist("pl-new", "sp-user-1"));
        spotify.PlaylistItems["pl-new"] = [.. Enumerable.Range(1, 45).Select(i => FakeSpotify.PlaylistItem(FakeSpotify.Track($"new-{i}", "al-new", "sp-fraud")))];
        spotify.FollowedArtistIds.Add("sp-fraud");
        await SyncAsync(spotify, protector, "old_playlist_fan");
        await DetectAsync();

        var flag = Assert.Single((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/fraud-flags", _staff), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray());
        Assert.Equal(("old_playlist_fan", "rule", "other"), (flag.GetProperty("user").GetProperty("handle").GetString(),
            flag.GetProperty("source").GetString(), flag.GetProperty("reason").GetString()));
        var burst = Assert.Single(Assert.Single(flag.GetProperty("evidence").GetProperty("rules").EnumerateObject(),
            r => r.Name == "burst_engagement").Value.EnumerateArray());
        Assert.Equal((_artistId, 46, 46), (burst.GetProperty("artist_id").GetGuid(), burst.GetProperty("saves").GetInt32(),
            burst.GetProperty("all_saves").GetInt32()));
        var kinds = burst.GetProperty("kinds");
        Assert.Equal((0, 45, 0, 1), (kinds.GetProperty("liked").GetInt32(), kinds.GetProperty("playlist").GetInt32(),
            kinds.GetProperty("album").GetInt32(), kinds.GetProperty("follow").GetInt32()));
    }

    private async Task SyncAsync(FakeSpotify spotify, SpotifyTokenProtector protector, string handle)
    {
        await using var connection = await _db.OpenConnectionAsync();
        var userId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM users WHERE handle = @handle", new { handle });
        var job = new SpotifySyncJob(_db, spotify.Client(), protector, Options.Create(FakeSpotify.Options()), NullLogger<SpotifySyncJob>.Instance);
        await job.RunAsync(new ClaimedJob(1, SpotifySyncJob.JobKind, JsonSerializer.Serialize(new { user_id = userId }), 1, 10),
            CancellationToken.None);
    }

    private async Task DetectAsync()
    {
        var job = new DetectFraudJob(_db, NullLogger<DetectFraudJob>.Instance);
        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);
    }

    [Fact]
    public async Task An_upheld_rule_flag_holds_the_fan_out_of_the_draw()
    {
        var allocationId = await AllocationAsync(tickets: 1);
        var bot = await FanAsync("upheld_bot", score: 99, "us-il-chicago");
        var other = await FanAsync("next_in_line", score: 50, "us-il-chicago");
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", bot), HttpStatusCode.Created);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{allocationId}/registration", other), HttpStatusCode.Created);
        Guid flagId;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            flagId = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO fraud_flags (user_id, reason, evidence, source)
                SELECT id, 'bot', '{"rules": {"round_the_clock": {"days_over": 4}}}', 'rule' FROM users WHERE handle = 'upheld_bot'
                RETURNING id
                """);
        }

        var bot2 = await TokenAsync("upheld_bot");
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/fraud-review", bot2), HttpStatusCode.OK))
            .GetProperty("flags").EnumerateArray());
        var upheld = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/fraud-flags/{flagId}/decision", _staff,
            new { decision = "uphold", note = "Checked: no human listens 24/7." }), HttpStatusCode.OK);
        Assert.NotEqual(JsonValueKind.Null, upheld.GetProperty("shown_to_fan_at").ValueKind);

        // Upheld by a person: now the fan is told, sees it, and can appeal; registering says under_review.
        var notices = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/notifications", bot2), HttpStatusCode.OK);
        Assert.Equal("fraud_hold", Assert.Single(notices.GetProperty("items").EnumerateArray()).GetProperty("kind").GetString());
        var review = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/fraud-review", bot2), HttpStatusCode.OK);
        Assert.True(review.GetProperty("on_hold").GetBoolean());
        Assert.True(Assert.Single(review.GetProperty("flags").EnumerateArray()).GetProperty("can_appeal").GetBoolean());
        var other2 = await AllocationAsync(tickets: 1);
        Assert.Equal("under_review", (await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/allocations/{other2}/registration", bot2),
            HttpStatusCode.Forbidden)).GetProperty("code").GetString());
        await DrawAsync(allocationId);

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/registrations", other), HttpStatusCode.OK);
        Assert.Equal("selected", mine.GetProperty("items")[0].GetProperty("registration").GetProperty("status").GetString());
        var audit = await AuditAsync("?handle=upheld_bot");
        Assert.Equal("draw_held_out", audit[0].GetProperty("action").GetString());
    }

    // ------------------------------------------------------------------ helpers

    private async Task<JsonElement> RaiseAsync(string handle, string reason, string? note = null, object? evidence = null) =>
        await JsonAsync(await SendAsync(HttpMethod.Post, "/v1/admin/fraud-flags", _staff, new { handle, reason, note, evidence }),
            HttpStatusCode.Created);

    private async Task<List<JsonElement>> AuditAsync(string query) =>
        [.. (await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/admin/eligibility-audit{query}", _staff), HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray()];

    private async Task DrawAsync(Guid allocationId)
    {
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
                UPDATE allocations SET registration_opens_at = now() - interval '2 hours', registration_closes_at = now() - interval '1 hour'
                 WHERE id = @allocationId;
                ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
                """,
                new { allocationId });
        }

        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/allocations/{allocationId}/selection", _staff), HttpStatusCode.Accepted);
        await Draws.RunQueuedAsync(_db);
    }

    private async Task<Guid> AllocationAsync(int tickets)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO allocations (event_id, tickets, face_value_cents, min_raw_score, registration_opens_at, registration_closes_at, published_at)
            VALUES (@_eventId, @tickets, 4500, 0, now() - interval '1 hour', now() + interval '5 days', now())
            RETURNING id
            """,
            new { _eventId, tickets });
    }

    private async Task<string> TokenAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        // 201 for a new account, 200 when signing in again.
        return (await JsonAsync(response, response.StatusCode == HttpStatusCode.OK ? HttpStatusCode.OK : HttpStatusCode.Created))
            .GetProperty("access_token").GetString()!;
    }

    /// <summary>A signed-in fan with the given hub cities and, unless null, a score for the artist.</summary>
    private async Task<string> FanAsync(string handle, decimal? score, params string[] hubs)
    {
        var token = await TokenAsync(handle);
        if (hubs.Length > 0)
        {
            await JsonAsync(await SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token, new { city_ids = hubs }), HttpStatusCode.OK);
        }

        if (score is not null)
        {
            await using var connection = await _db.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                SELECT id, @_artistId, @score, least(@score, 100), true, 0.9 FROM users WHERE handle = @handle
                """,
                new { _artistId, score, handle });
        }

        return token;
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
