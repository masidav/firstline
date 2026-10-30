using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;

namespace FandomScore.Api.Features.Privacy;

/// <summary>
/// openapi <c>GET /me/export</c> ("Download my data"): everything the app holds about the caller, as one JSON file,
/// grouped by area. Each section names its columns explicitly, so a column added later is not exported until someone
/// decides it should be. Secrets, token hashes, other people's private data, and staff-internal fields are left out;
/// <c>README.md</c> lists what is in and what is out, and why.
/// </summary>
public static class DataExportEndpoints
{
    public const string ExportRateLimit = "data-export";

    /// <summary>Exports per user per hour (openapi.yaml rate limit table).</summary>
    public const int ExportsPerHour = 3;

    /// <summary>Bumped when a section is renamed or removed (adding one doesn't change it).</summary>
    public const int FormatVersion = 1;

    public static IServiceCollection AddDataExport(this IServiceCollection services) =>
        services.Configure<RateLimiterOptions>(limiter => limiter.AddPolicy(ExportRateLimit, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = ExportsPerHour,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                })));

    public static void MapDataExport(this RouteGroupBuilder v1) =>
        v1.MapGet("/me/export", ExportAsync).RequireRateLimiting(ExportRateLimit);

    private static string Rows(string select) => $"SELECT coalesce(json_agg(t), '[]'::json)::text FROM ({select}) t";

    private static string Row(string select) => $"SELECT row_to_json(t)::text FROM ({select}) t";

    // Area -> section -> query returning one JSON value. @me is the caller, @session their session, @links the signed
    // links to their proof files.
    private static readonly (string Area, (string Section, string Sql)[] Sections)[] Areas =
    [
        ("account", [
            ("profile", Row(
                """
                SELECT u.id, u.handle::text AS handle, u.email::text AS email, u.display_name, u.avatar_url, u.is_staff,
                       u.is_artist, u.band_url, u.instagram_handle, u.x_handle, u.presale_reminders,
                       json_build_object('tickets', u.push_tickets, 'presales', u.push_presales, 'staff', u.push_staff) AS push,
                       u.hubs_changed_at, u.created_at
                FROM users u WHERE u.id = @me AND u.deleted_at IS NULL
                """)),
            ("sign_in_methods", Rows(
                "SELECT provider::text AS provider, created_at, last_used_at FROM auth_identities WHERE user_id = @me ORDER BY created_at")),
            ("sessions", Rows(
                """
                SELECT created_at, last_refreshed_at, expires_at, revoked_at, revoked_reason, id = @session AS current
                FROM auth_sessions WHERE user_id = @me ORDER BY created_at DESC
                """)),
            ("hub_cities", Rows(
                """
                SELECT h.position, h.city_id, c.name, c.region, c.country
                FROM user_hub_cities h JOIN cities c ON c.id = h.city_id WHERE h.user_id = @me ORDER BY h.position
                """)),
            ("push_devices", Rows(
                """
                SELECT id, platform, created_at, last_seen_at, disabled_at, disabled_reason
                FROM push_devices WHERE user_id = @me ORDER BY created_at
                """)),
            ("organizations", Rows(
                """
                SELECT o.id, o.name, o.kind, m.role, m.added_at
                FROM organization_members m JOIN organizations o ON o.id = m.organization_id
                WHERE m.user_id = @me ORDER BY m.added_at
                """)),
        ]),
        ("spotify", [
            ("connection", Row(
                """
                SELECT spotify_user_id, scopes, connected_at, last_synced_at, sync_state, sync_error, tracked_artists_found,
                       top_artists_added, plays_polled_at
                FROM spotify_connections WHERE user_id = @me
                """)),
            ("artist_signals", Rows(
                """
                SELECT a.id AS artist_id, a.name AS artist_name, s.liked_tracks, s.playlist_tracks, s.distinct_albums,
                       s.top_artist_rank, s.top_artist_rank_short, s.top_artist_rank_long, s.top_tracks, s.playlists_featuring,
                       s.dedicated_playlists, s.followed, s.followed_since, s.followed_since_baseline, s.recent_plays, s.synced_at
                FROM user_artist_signals s JOIN artists a ON a.id = s.artist_id WHERE s.user_id = @me ORDER BY a.name
                """)),
            ("saved_tracks", Rows(
                """
                SELECT t.spotify_track_id, t.title AS track_title, al.title AS album_title, a.name AS artist_name, l.source, l.saved_at,
                       l.saved_at_baseline
                FROM user_track_likes l JOIN tracks t ON t.id = l.track_id JOIN albums al ON al.id = t.album_id
                JOIN artists a ON a.id = al.artist_id
                WHERE l.user_id = @me ORDER BY a.name, al.title, t.title, l.source
                """)),
            ("saved_albums", Rows(
                """
                SELECT al.spotify_album_id, al.title AS album_title, a.name AS artist_name, s.saved_at
                FROM user_album_saves s JOIN albums al ON al.id = s.album_id JOIN artists a ON a.id = al.artist_id
                WHERE s.user_id = @me ORDER BY a.name, al.title
                """)),
            ("plays", Rows(
                """
                SELECT p.played_at, p.spotify_track_id,
                       (SELECT t.title FROM tracks t WHERE t.spotify_track_id = p.spotify_track_id) AS track_title,
                       a.name AS artist_name
                FROM user_plays p JOIN artists a ON a.id = p.artist_id WHERE p.user_id = @me ORDER BY p.played_at DESC
                """)),
        ]),
        ("fandom", [
            ("scores", Rows(
                """
                SELECT a.id AS artist_id, a.name AS artist_name, s.raw_score, s.percentile, s.is_estimated, s.baseline_weight,
                       s.breakdown, s.computed_at
                FROM user_artist_scores s JOIN artists a ON a.id = s.artist_id WHERE s.user_id = @me ORDER BY a.name
                """)),
            ("points", Rows(
                """
                SELECT e.id, a.id AS artist_id, a.name AS artist_name, e.rule_key, e.points, e.source_type, e.source_id,
                       e.awarded_at, e.reversed_at
                FROM score_events e JOIN artists a ON a.id = e.artist_id WHERE e.user_id = @me ORDER BY e.id
                """)),
            ("share_cards", Rows(
                """
                SELECT c.id, a.name AS artist_name, c.raw_score, c.percentile, c.image_url, c.shared_to, c.created_at
                FROM share_cards c JOIN artists a ON a.id = c.artist_id WHERE c.user_id = @me ORDER BY c.created_at
                """)),
            ("artists_added", Rows(
                "SELECT id, name, spotify_artist_id, added_via, tracked_at FROM artists WHERE added_by = @me ORDER BY tracked_at")),
            ("outbound_clicks", Rows(
                """
                SELECT a.name AS artist_name, c.event_id, c.kind, c.partner, c.clicked_at
                FROM outbound_clicks c JOIN artists a ON a.id = c.artist_id WHERE c.user_id = @me ORDER BY c.clicked_at
                """)),
        ]),
        ("journal", [
            ("album_logs", Rows(
                """
                SELECT l.id, al.id AS album_id, al.title AS album_title, a.name AS artist_name, l.rating, l.review, l.listened_on,
                       l.is_relisten, l.visibility, l.created_at, l.updated_at, l.deleted_at, l.removed_at
                FROM album_logs l JOIN albums al ON al.id = l.album_id JOIN artists a ON a.id = al.artist_id
                WHERE l.user_id = @me ORDER BY l.created_at
                """)),
            ("lists", Rows(
                """
                SELECT l.id, l.title, l.description, l.visibility, l.created_at,
                       (SELECT coalesce(json_agg(json_build_object('position', i.position, 'album_id', i.album_id,
                                                                   'album_title', al.title, 'note', i.note) ORDER BY i.position), '[]')
                          FROM list_items i JOIN albums al ON al.id = i.album_id WHERE i.list_id = l.id) AS items
                FROM lists l WHERE l.user_id = @me ORDER BY l.created_at
                """)),
            ("comments", Rows(
                """
                SELECT id, album_log_id, parent_id, body, created_at, edited_at, deleted_at
                FROM comments WHERE user_id = @me ORDER BY created_at
                """)),
        ]),
        ("activity", [
            ("prompt_answers", Rows(
                """
                SELECT pa.id, p.day AS prompt_day, p.text AS prompt, pa.body, pa.visibility, pa.created_at, pa.updated_at,
                       pa.deleted_at, pa.removed_at
                FROM prompt_answers pa JOIN prompts p ON p.id = pa.prompt_id WHERE pa.user_id = @me ORDER BY p.day
                """)),
            // The right answer only once the fan has answered: an open question stays open (migration 0035).
            ("trivia", Rows(
                """
                SELECT q.id, q.day, a.name AS artist_name, q.kind, q.prompt, q.options,
                       CASE WHEN q.answered_at IS NOT NULL THEN q.answer_index END AS answer_index,
                       q.chosen_index, q.correct, q.answered_at, q.created_at
                FROM trivia_questions q JOIN artists a ON a.id = q.artist_id WHERE q.user_id = @me ORDER BY q.day
                """)),
            ("reactions", Rows(
                "SELECT target_type, target_id, reaction, created_at FROM post_reactions WHERE user_id = @me ORDER BY created_at")),
            ("reports_filed", Rows(
                """
                SELECT id, target_type, target_id, reason, details, created_at, resolved_at, resolution
                FROM reports WHERE reporter_id = @me ORDER BY created_at
                """)),
        ]),
        ("proof", [
            ("submissions", Rows(
                """
                SELECT s.id, a.id AS artist_id, a.name AS artist_name, s.event_id, e.name AS event_name, s.kind, s.status,
                       s.caption, s.concert_date, s.concert_venue, s.concert_city, s.rejection_reason, s.submitted_at,
                       s.verified_at, s.rejected_at, s.archived_at, s.shared_at, s.removed_at,
                       (SELECT coalesce(json_agg(json_build_object(
                                 'id', f.id, 'mime_type', f.mime_type, 'bytes', f.bytes, 'uploaded_at', f.uploaded_at,
                                 'redacted', f.redacted, 'redaction_state', f.redaction_state, 'storage_tier', f.tier,
                                 'archived_at', f.archived_at, 'url', @links::jsonb ->> f.id::text) ORDER BY f.uploaded_at), '[]')
                          FROM submission_files f WHERE f.submission_id = s.id) AS files
                FROM submissions s JOIN artists a ON a.id = s.artist_id LEFT JOIN events e ON e.id = s.event_id
                WHERE s.user_id = @me ORDER BY s.submitted_at
                """)),
            ("uploads", Rows(
                """
                SELECT id, purpose, mime_type, declared_bytes, bytes, created_at, uploaded_at, expires_at, submission_id
                FROM uploads WHERE user_id = @me ORDER BY created_at
                """)),
        ]),
        ("social", [
            ("following", Rows(
                """
                SELECT u.handle::text AS handle, f.created_at AS followed_at
                FROM follows f JOIN users u ON u.id = f.followee_id AND u.deleted_at IS NULL
                WHERE f.follower_id = @me ORDER BY f.created_at
                """)),
            ("followers", Rows(
                """
                SELECT u.handle::text AS handle, f.created_at AS followed_at
                FROM follows f JOIN users u ON u.id = f.follower_id AND u.deleted_at IS NULL
                WHERE f.followee_id = @me ORDER BY f.created_at
                """)),
            ("grab_bag", Rows(
                """
                SELECT g.position, g.spotify_track_id, g.title, a.name AS artist_name, g.album_title, g.cover_url, g.added_at,
                       g.refreshed_at
                FROM grab_bag_songs g JOIN artists a ON a.id = g.artist_id WHERE g.user_id = @me ORDER BY g.position
                """)),
        ]),
        ("tickets", [
            ("registrations", Rows(
                """
                SELECT r.id, r.allocation_id, al.name AS allocation_name, r.event_id, e.name AS event_name,
                       e.starts_at AS event_starts_at, r.hub_city_ids, r.hub_match, r.percentile, r.raw_score, r.is_estimated,
                       r.baseline_weight, r.score_computed_at, r.registered_at, r.withdrawn_at, r.withdrawn_reason, r.plus_one,
                       g.handle::text AS guest_handle, r.guest_named_at, r.outcome, r.selection_rank, r.seats
                FROM allocation_registrations r JOIN allocations al ON al.id = r.allocation_id JOIN events e ON e.id = r.event_id
                LEFT JOIN users g ON g.id = r.guest_user_id AND g.deleted_at IS NULL
                WHERE r.user_id = @me ORDER BY r.registered_at
                """)),
            // Shows another fan named the caller as their +1 for: the show and the host's handle, nothing of the host's standing.
            ("guest_of", Rows(
                """
                SELECT r.event_id, e.name AS event_name, e.starts_at AS event_starts_at, h.handle::text AS host_handle,
                       r.guest_named_at
                FROM allocation_registrations r JOIN events e ON e.id = r.event_id
                LEFT JOIN users h ON h.id = r.user_id AND h.deleted_at IS NULL
                WHERE r.guest_user_id = @me ORDER BY r.guest_named_at
                """)),
            // Never the ticket token: it is the ticket itself, shown in the app once delivered.
            ("reservations", Rows(
                """
                SELECT res.id, res.registration_id, res.allocation_id, res.event_id, e.name AS event_name,
                       e.starts_at AS event_starts_at, res.seats, res.face_value_cents, res.currency, res.deliver_at,
                       res.created_at, res.returned_at, res.cancelled_at, res.cancelled_reason
                FROM reservations res JOIN events e ON e.id = res.event_id WHERE res.user_id = @me ORDER BY res.created_at
                """)),
            // Fraud reviews as the fan sees them: never the evidence, staff notes, or who on staff decided.
            ("account_reviews", Rows(
                """
                SELECT f.id, f.reason, f.status, f.raised_at, f.decided_at, ap.message AS appeal_message,
                       ap.filed_at AS appeal_filed_at, ap.status AS appeal_status, ap.decided_at AS appeal_decided_at,
                       ap.response AS appeal_response
                FROM fraud_flags f LEFT JOIN fraud_appeals ap ON ap.flag_id = f.id
                WHERE f.user_id = @me AND f.shown_to_fan_at IS NOT NULL ORDER BY f.raised_at
                """)),
            // The eligibility audit about the fan; the inputs of flag decisions can hold staff notes, so they're left out.
            // A flag the fan hasn't been shown (an unreviewed rule flag, migration 0044) leaves no trace: its own rows are
            // skipped and refusals it caused don't name it.
            ("eligibility_decisions", Rows(
                """
                SELECT x.at, x.action, x.reason, x.allocation_id, x.registration_id,
                       CASE WHEN f.shown_to_fan_at IS NOT NULL THEN x.flag_id END AS flag_id, x.appeal_id,
                       CASE WHEN x.action LIKE 'flag_%' THEN NULL ELSE x.inputs - 'flag_id' END AS inputs
                FROM eligibility_audit x LEFT JOIN fraud_flags f ON f.id = x.flag_id
                WHERE x.user_id = @me AND (x.flag_id IS NULL OR f.shown_to_fan_at IS NOT NULL OR x.action = 'registration_blocked')
                ORDER BY x.id
                """)),
        ]),
    ];

    // Fan notices only: staff notices (staff_*) are work items about other people's posts and shows.
    private static readonly string NotificationsSql = Rows(
        """
        SELECT id, kind, subject_id, data, created_at, read_at
        FROM notifications WHERE user_id = @me AND kind NOT LIKE 'staff\_%' ORDER BY created_at
        """);

    private static async Task<IResult> ExportAsync(
        ClaimsPrincipal user, HttpRequest request, HttpResponse response, NpgsqlDataSource db, SignedUrls urls, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var me = user.UserId();
        var session = user.SessionId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        // One snapshot for the whole file, and timestamps written in UTC.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await connection.ExecuteAsync("SET TRANSACTION READ ONLY; SET LOCAL TimeZone = 'UTC'", transaction: transaction);

        // Signed links (an hour, as in GET /me/submissions) to the caller's own proof files.
        var fileIds = await connection.QueryAsync<Guid>(
            "SELECT f.id FROM submission_files f JOIN submissions s ON s.id = f.submission_id WHERE s.user_id = @me",
            new { me }, transaction);
        var links = JsonSerializer.Serialize(fileIds.ToDictionary(id => id.ToString(), id => UploadEndpoints.FileUrl(request, urls, id)));
        var parameters = new { me, session, links };

        var now = time.GetUtcNow().UtcDateTime;
        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format_version", FormatVersion);
            writer.WriteString("exported_at", now);
            writer.WriteString("user_id", me);
            foreach (var (area, sections) in Areas)
            {
                writer.WriteStartObject(area);
                foreach (var (section, sql) in sections)
                {
                    var json = await connection.ExecuteScalarAsync<string?>(sql, parameters, transaction);
                    if (json is null && section == "profile")
                    {
                        throw ApiProblemException.Unauthorized("The account no longer exists.");
                    }

                    writer.WritePropertyName(section);
                    writer.WriteRawValue(json ?? "null");
                }

                writer.WriteEndObject();
            }

            writer.WritePropertyName("notifications");
            writer.WriteRawValue(await connection.ExecuteScalarAsync<string>(NotificationsSql, parameters, transaction) ?? "[]");
            writer.WriteEndObject();
        }

        await transaction.CommitAsync(cancellationToken);
        response.Headers.CacheControl = "no-store";
        var fileName = $"fandom-score-export-{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json";
        return Results.File(buffer.ToArray(), "application/json", fileName);
    }
}
