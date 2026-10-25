using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Dapper;
using FandomScore.Api.Features.Allocations;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.GrabBag;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Feed;

/// <summary>
/// openapi <c>GET /feed</c>, the home feed: one vertical scroll mixing, at random, shared photos and album logs from
/// every fan, and from the fans the caller follows their followers-only logs, grab-bag songs, and top fandom stats;
/// plus upcoming shows of the caller's artists (or in their hub cities) and the caller's own live registrations.
/// Each source contributes its most recent items (a pool); the pools are shuffled by a seeded hash and dealt out
/// by type weight, so one <c>seed</c> gives one stable order to page through, every page mixes the types, and a new
/// seed (the app picks one per refresh) gives a new mix.
/// Deleted accounts, deleted or taken-down logs, and inactive artists are left out.
/// </summary>
public static partial class FeedEndpoints
{
    public const int ReviewExcerptLength = 280;

    /// <summary>How many of the newest items each source contributes to the mix.</summary>
    public const int PoolSize = 100;

    /// <summary>Shows further out than this stay off the feed.</summary>
    public const int ConcertHorizonDays = 120;

    public static void MapFeed(this RouteGroupBuilder v1) => v1.MapGet("/feed", GetAsync);

    /// <summary>Where a concert photo was taken: the listed show (with its name) or the show the fan entered.</summary>
    public sealed record FeedConcert(string? Name, string? Venue, string? City, DateOnly Date);

    public sealed record FeedPhoto(
        string Kind, string? Caption, FeedConcert? Concert, IReadOnlyList<SubmissionEndpoints.SubmissionFile> Files);

    public sealed record FeedAlbumRef(Guid Id, string Title, string? CoverUrl);

    public sealed record FeedLog(
        FeedAlbumRef Album, int? Rating, string? Review, bool ReviewTruncated, bool IsRelisten, DateOnly ListenedOn, string Visibility);

    /// <summary>An upcoming show. <c>Allocation</c> is the fan allocation fans can register for (or will be able to), if any.</summary>
    public sealed record FeedShow(AllocationEndpoints.Show Event, AllocationEndpoints.Allocation? Allocation, bool Registered);

    /// <summary>
    /// A number from a fan the caller follows. <c>Kind</c>: <c>top_fan</c> (their place among the artist's fans here:
    /// <c>Rank</c> of <c>Fans</c>, <c>Percentile</c>), <c>albums_logged</c>, <c>concert_photos</c>, <c>merch</c>, or
    /// <c>points</c> (ledger points), with the count in <c>Value</c>.
    /// </summary>
    public sealed record FeedStat(string Kind, decimal Value, decimal? Percentile, int? Rank, int? Fans, bool? IsEstimated);

    /// <summary>
    /// <c>Type</c> says which of the payloads is set. <c>User</c> is null for a show; <c>Artist</c> is null for a stat
    /// that isn't about one artist. <c>Following</c>: the caller follows the item's user.
    /// </summary>
    /// <summary>An answer to a music prompt (migration 0025): the prompt it answers and the answer.</summary>
    public sealed record FeedPromptAnswer(Guid PromptId, string PromptText, DateOnly Day, string Body, string Visibility);

    public sealed record FeedItem(
        string Type, Guid Id, DateTime At, SubmissionEndpoints.UserRef? User, ScoringEndpoints.ArtistRef? Artist, bool Following,
        FeedPhoto? Photo, FeedLog? Log, GrabBagEndpoints.GrabBagSong? GrabBag, FeedShow? Concert,
        AllocationEndpoints.RegistrationCard? Registration, FeedStat? Stat, FeedPromptAnswer? PromptAnswer = null);

    public sealed record FeedPage(IReadOnlyList<FeedItem> Items, string? NextCursor, string Seed);

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user, int? limit, string? cursor, string? seed, HttpRequest request, NpgsqlDataSource db, SignedUrls urls,
        IConfiguration configuration, TimeProvider time, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        if (limit is < 1 or > 100)
        {
            errors.Add(new FieldError("limit", "Use a limit from 1 to 100."));
        }

        var offset = 0;
        if (cursor is not null && !PageCursor.TryRead(cursor, out offset))
        {
            errors.Add(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        if (seed is not null && !SeedPattern().IsMatch(seed))
        {
            errors.Add(new FieldError("seed", "Use 1 to 64 letters, digits, dashes, or underscores."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        // Without a seed the mix still pages consistently within a day.
        seed ??= time.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var me = user.UserId();
        var pageSize = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<CandidateRow>(CandidatesSql,
            new { me, seed, take = pageSize + 1, offset, pool = PoolSize, horizonDays = ConcertHorizonDays })).ToList();
        var page = rows.Take(pageSize).ToList();
        var next = rows.Count > pageSize ? PageCursor.Write(offset + pageSize) : null;
        if (page.Count == 0)
        {
            return Results.Ok(new FeedPage([], next, seed));
        }

        var users = (await connection.QueryAsync<UserRow>(
                """
                SELECT u.id AS Id, u.handle::text AS Handle, u.display_name AS DisplayName, u.avatar_url AS AvatarUrl,
                       EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = u.id) AS Following
                FROM users u WHERE u.id = ANY(@ids)
                """,
                new { me, ids = page.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).Distinct().ToArray() }))
            .ToDictionary(u => u.Id);
        var artists = (await connection.QueryAsync<ScoringEndpoints.ArtistRef>(
                "SELECT id AS Id, name AS Name, image_url AS ImageUrl FROM artists WHERE id = ANY(@ids)",
                new { ids = page.Where(r => r.ArtistId is not null).Select(r => r.ArtistId!.Value).Distinct().ToArray() }))
            .ToDictionary(a => a.Id);

        var photos = await LoadPhotosAsync(connection, Ids(page, "photo"), request, urls);
        var logs = await LoadLogsAsync(connection, Ids(page, "album_log"));
        var songs = await LoadSongsAsync(connection, Ids(page, "grab_bag"));
        var shows = await LoadShowsAsync(connection, me, Ids(page, "concert"));
        var registrations = await LoadRegistrationsAsync(connection, me, Ids(page, "registration"), configuration);
        var answers = await LoadPromptAnswersAsync(connection, Ids(page, "prompt_answer"));

        var items = new List<FeedItem>(page.Count);
        foreach (var row in page)
        {
            var userRow = row.UserId is { } userId ? users.GetValueOrDefault(userId) : null;
            var userRef = userRow is null ? null : new SubmissionEndpoints.UserRef(userRow.Handle, false, userRow.DisplayName, userRow.AvatarUrl);
            var artist = row.ArtistId is { } artistId ? artists.GetValueOrDefault(artistId) : null;
            FeedItem? item = row.Type switch
            {
                "photo" when photos.TryGetValue(row.Id, out var photo) => Item(row, userRef, artist, userRow) with { Photo = photo },
                "album_log" when logs.TryGetValue(row.Id, out var log) => Item(row, userRef, artist, userRow) with { Log = log },
                "grab_bag" when songs.TryGetValue(row.Id, out var song) => Item(row, userRef, artist, userRow) with { GrabBag = song },
                "concert" when shows.TryGetValue(row.Id, out var show) => Item(row, userRef, artist, userRow) with { Concert = show },
                "registration" when registrations.TryGetValue(row.Id, out var card) => Item(row, userRef, artist, userRow) with { Registration = card },
                "prompt_answer" when answers.TryGetValue(row.Id, out var answer) => Item(row, userRef, artist, userRow) with { PromptAnswer = answer },
                "stat" => Item(row, userRef, artist, userRow) with
                {
                    Stat = new FeedStat(row.StatKind!, row.StatValue ?? 0, row.StatPercentile, row.StatRank, row.StatFans, row.StatIsEstimated),
                },
                _ => null,   // gone between the two queries
            };
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return Results.Ok(new FeedPage(items, next, seed));
    }

    private static FeedItem Item(CandidateRow row, SubmissionEndpoints.UserRef? user, ScoringEndpoints.ArtistRef? artist, UserRow? userRow) =>
        new(row.Type, row.Id, row.At, user, artist, userRow?.Following ?? false, null, null, null, null, null, null);

    private static Guid[] Ids(List<CandidateRow> page, string type) => [.. page.Where(r => r.Type == type).Select(r => r.Id)];

    // Each branch is one source's pool of newest items; the outer query shuffles them with the seed. Stats get a
    // stable id from the fan and the stat, so they page like everything else.
    private const string CandidatesSql =
        """
        WITH my_follows AS (SELECT followee_id FROM follows WHERE follower_id = @me),
        candidates AS (
          (SELECT 'photo' AS type, s.id, s.shared_at AS at, s.user_id, s.artist_id,
                  NULL::text AS stat_kind, NULL::numeric AS stat_value, NULL::numeric AS stat_percentile,
                  NULL::int AS stat_rank, NULL::int AS stat_fans, NULL::boolean AS stat_is_estimated
             FROM submissions s JOIN users u ON u.id = s.user_id AND u.deleted_at IS NULL
             JOIN artists a ON a.id = s.artist_id AND a.status = 'active'
            WHERE s.shared_at IS NOT NULL AND s.status = 'verified'
            ORDER BY s.shared_at DESC LIMIT @pool)
          UNION ALL
          (SELECT 'album_log', l.id, l.created_at, l.user_id, al.artist_id, NULL, NULL, NULL, NULL, NULL, NULL
             FROM album_logs l JOIN albums al ON al.id = l.album_id
             JOIN users u ON u.id = l.user_id AND u.deleted_at IS NULL
             JOIN artists a ON a.id = al.artist_id AND a.status = 'active'
            WHERE l.deleted_at IS NULL AND l.removed_at IS NULL
              AND (l.visibility = 'public'
                   OR (l.visibility = 'followers' AND (l.user_id = @me OR l.user_id IN (SELECT followee_id FROM my_follows))))
            ORDER BY l.created_at DESC LIMIT @pool)
          UNION ALL
          (SELECT 'prompt_answer', pa.id, pa.created_at, pa.user_id, NULL::uuid, NULL, NULL, NULL, NULL, NULL, NULL
             FROM prompt_answers pa JOIN prompts p ON p.id = pa.prompt_id
             JOIN users u ON u.id = pa.user_id AND u.deleted_at IS NULL
            WHERE pa.deleted_at IS NULL AND pa.removed_at IS NULL AND p.day >= (now() AT TIME ZONE 'UTC')::date - 7
              AND (pa.visibility = 'public' OR pa.user_id = @me OR pa.user_id IN (SELECT followee_id FROM my_follows))
            ORDER BY pa.created_at DESC LIMIT @pool)
          UNION ALL
          (SELECT 'grab_bag', g.id, g.added_at, g.user_id, g.artist_id, NULL, NULL, NULL, NULL, NULL, NULL
             FROM grab_bag_songs g JOIN users u ON u.id = g.user_id AND u.deleted_at IS NULL
             JOIN artists a ON a.id = g.artist_id AND a.status IN ('onboarding', 'active')
            WHERE g.user_id IN (SELECT followee_id FROM my_follows)
            ORDER BY g.added_at DESC LIMIT @pool)
          UNION ALL
          (SELECT 'concert', e.id, e.starts_at, NULL::uuid, e.artist_id, NULL, NULL, NULL, NULL, NULL, NULL
             FROM events e JOIN artists a ON a.id = e.artist_id AND a.status = 'active'
            WHERE e.starts_at > now() AND e.starts_at < now() + make_interval(days => @horizonDays)
              AND (EXISTS (SELECT 1 FROM user_artist_scores sc WHERE sc.user_id = @me AND sc.artist_id = e.artist_id)
                   OR EXISTS (SELECT 1 FROM user_hub_cities h WHERE h.user_id = @me AND h.city_id = e.city_id))
            ORDER BY e.starts_at LIMIT @pool)
          UNION ALL
          (SELECT 'registration', r.id, r.registered_at, r.user_id, e.artist_id, NULL, NULL, NULL, NULL, NULL, NULL
             FROM allocation_registrations r JOIN events e ON e.id = r.event_id
            WHERE r.user_id = @me AND r.withdrawn_at IS NULL AND e.starts_at > now()
            ORDER BY r.registered_at DESC LIMIT @pool)
          UNION ALL
          (SELECT 'stat', md5('top_fan:' || sc.user_id || ':' || sc.artist_id)::uuid, sc.computed_at, sc.user_id, sc.artist_id,
                  'top_fan', sc.raw_score, sc.percentile,
                  (SELECT count(*) FROM user_artist_scores o WHERE o.artist_id = sc.artist_id AND o.raw_score > sc.raw_score)::int + 1,
                  (SELECT count(*) FROM user_artist_scores o WHERE o.artist_id = sc.artist_id)::int,
                  sc.is_estimated
             FROM user_artist_scores sc JOIN artists a ON a.id = sc.artist_id AND a.status = 'active'
             JOIN users u ON u.id = sc.user_id AND u.deleted_at IS NULL
            WHERE sc.user_id IN (SELECT followee_id FROM my_follows) AND sc.percentile >= 50
            ORDER BY sc.percentile DESC LIMIT @pool)
          UNION ALL
          (SELECT 'stat', md5('albums_logged:' || l.user_id)::uuid, max(l.created_at), l.user_id, NULL,
                  'albums_logged', count(DISTINCT l.album_id), NULL, NULL, NULL, NULL
             FROM album_logs l JOIN users u ON u.id = l.user_id AND u.deleted_at IS NULL
            WHERE l.user_id IN (SELECT followee_id FROM my_follows) AND l.deleted_at IS NULL AND l.removed_at IS NULL
              AND l.visibility <> 'private'
            GROUP BY l.user_id HAVING count(DISTINCT l.album_id) >= 3
            ORDER BY max(l.created_at) DESC LIMIT @pool)
          UNION ALL
          (SELECT 'stat', md5(s.kind::text || ':' || s.user_id)::uuid, max(s.verified_at), s.user_id, NULL,
                  CASE s.kind::text WHEN 'merch' THEN 'merch' ELSE 'concert_photos' END, count(*), NULL, NULL, NULL, NULL
             FROM submissions s JOIN users u ON u.id = s.user_id AND u.deleted_at IS NULL
            WHERE s.user_id IN (SELECT followee_id FROM my_follows) AND s.status = 'verified'
              AND s.kind::text IN ('merch', 'concert_photo')
            GROUP BY s.user_id, s.kind
            ORDER BY max(s.verified_at) DESC LIMIT @pool)
          UNION ALL
          (SELECT 'stat', md5('points:' || se.user_id)::uuid, max(se.awarded_at), se.user_id, NULL,
                  'points', sum(se.points), NULL, NULL, NULL, NULL
             FROM score_events se JOIN users u ON u.id = se.user_id AND u.deleted_at IS NULL
            WHERE se.user_id IN (SELECT followee_id FROM my_follows) AND se.reversed_at IS NULL
            GROUP BY se.user_id HAVING sum(se.points) >= 10
            ORDER BY max(se.awarded_at) DESC LIMIT @pool)
        )
        , hashed AS (
          SELECT c.*, hashtextextended(@seed || ':' || c.type || ':' || c.id::text, 0) AS h FROM candidates c
        )
        -- Within a type the seed decides the order. Across types, each type is dealt out at its weight (so many
        -- per round) with a seeded jitter inside the round, so a page is a real mix even when one source (album
        -- logs) has far more to offer; once a type runs out the others fill in.
        SELECT c.type AS Type, c.id AS Id, c.at AS At, c.user_id AS UserId, c.artist_id AS ArtistId, c.stat_kind AS StatKind,
               c.stat_value AS StatValue, c.stat_percentile AS StatPercentile, c.stat_rank AS StatRank, c.stat_fans AS StatFans,
               c.stat_is_estimated AS StatIsEstimated
        FROM hashed c
        JOIN (VALUES ('photo', 2), ('album_log', 3), ('grab_bag', 2), ('concert', 1), ('registration', 1), ('stat', 2),
                     ('prompt_answer', 2)) w (type, weight)
          ON w.type = c.type
        ORDER BY (row_number() OVER (PARTITION BY c.type ORDER BY c.h, c.id) - 1 + (c.h & 1023) / 1024.0) / w.weight, c.type, c.id
        LIMIT @take OFFSET @offset
        """;

    private static async Task<Dictionary<Guid, FeedPhoto>> LoadPhotosAsync(
        NpgsqlConnection connection, Guid[] ids, HttpRequest request, SignedUrls urls)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<PhotoRow>(
            """
            SELECT s.id AS Id, s.kind::text AS Kind, s.caption AS Caption, e.name AS ConcertName,
                   COALESCE(e.venue, s.concert_venue) AS ConcertVenue, COALESCE(e.city, s.concert_city) AS ConcertCity,
                   COALESCE(e.starts_at::date, s.concert_date) AS ConcertDate
            FROM submissions s LEFT JOIN events e ON e.id = s.event_id
            WHERE s.id = ANY(@ids)
            """,
            new { ids });
        var files = (await connection.QueryAsync<(Guid SubmissionId, Guid Id, string MimeType, bool Redacted)>(
                "SELECT submission_id, id, mime_type, redacted FROM submission_files WHERE submission_id = ANY(@ids) ORDER BY uploaded_at, id",
                new { ids }))
            .ToLookup(f => f.SubmissionId);
        return rows.ToDictionary(r => r.Id, r => new FeedPhoto(r.Kind, r.Caption,
            r.ConcertDate is { } date ? new FeedConcert(r.ConcertName, r.ConcertVenue, r.ConcertCity, date) : null,
            [.. files[r.Id].Select(f => new SubmissionEndpoints.SubmissionFile(f.Id, f.MimeType, UploadEndpoints.FileUrl(request, urls, f.Id), f.Redacted))]));
    }

    private static async Task<Dictionary<Guid, FeedLog>> LoadLogsAsync(NpgsqlConnection connection, Guid[] ids)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<LogRow>(
            """
            SELECT l.id AS Id, al.id AS AlbumId, al.title AS AlbumTitle, al.cover_url AS CoverUrl, l.rating AS Rating,
                   l.review AS Review, l.is_relisten AS IsRelisten, l.listened_on AS ListenedOn, l.visibility::text AS Visibility
            FROM album_logs l JOIN albums al ON al.id = l.album_id
            WHERE l.id = ANY(@ids)
            """,
            new { ids });
        return rows.ToDictionary(r => r.Id, r => new FeedLog(new FeedAlbumRef(r.AlbumId, r.AlbumTitle, r.CoverUrl), r.Rating,
            Excerpt(r.Review), r.Review is { Length: > ReviewExcerptLength }, r.IsRelisten, r.ListenedOn, r.Visibility));
    }

    private static async Task<Dictionary<Guid, GrabBagEndpoints.GrabBagSong>> LoadSongsAsync(NpgsqlConnection connection, Guid[] ids)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<GrabBagEndpoints.SongRow>(
            $"SELECT {GrabBagEndpoints.SongColumns} FROM grab_bag_songs g JOIN artists a ON a.id = g.artist_id WHERE g.id = ANY(@ids)",
            new { ids });
        return rows.ToDictionary(r => r.Id, GrabBagEndpoints.ToSong);
    }

    // Each show with the allocation fans can (or will be able to) register for: the earliest-opening one that is
    // published, not cancelled, and not closed. Whether the caller already holds a live registration for the show.
    private static async Task<Dictionary<Guid, FeedShow>> LoadShowsAsync(NpgsqlConnection connection, Guid me, Guid[] ids)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var shows = await connection.QueryAsync<AllocationEndpoints.Show>(
            $"SELECT {AllocationEndpoints.EventColumns} FROM events e LEFT JOIN cities c ON c.id = e.city_id WHERE e.id = ANY(@ids)",
            new { ids });
        var allocations = (await connection.QueryAsync<AllocationEndpoints.Allocation>(
                $"""
                 SELECT DISTINCT ON (a.event_id) {AllocationEndpoints.AllocationColumns} FROM allocations a
                 WHERE a.event_id = ANY(@ids) AND a.published_at IS NOT NULL AND a.cancelled_at IS NULL AND a.registration_closes_at > now()
                 ORDER BY a.event_id, a.registration_opens_at, a.created_at
                 """,
                new { ids }))
            .ToDictionary(a => a.EventId);
        var registered = (await connection.QueryAsync<Guid>(
                "SELECT event_id FROM allocation_registrations WHERE user_id = @me AND event_id = ANY(@ids) AND withdrawn_at IS NULL",
                new { me, ids }))
            .ToHashSet();
        return shows.ToDictionary(s => s.Id, s => new FeedShow(s, allocations.GetValueOrDefault(s.Id), registered.Contains(s.Id)));
    }

    private static async Task<Dictionary<Guid, AllocationEndpoints.RegistrationCard>> LoadRegistrationsAsync(
        NpgsqlConnection connection, Guid me, Guid[] ids, IConfiguration configuration)
    {
        var cards = new Dictionary<Guid, AllocationEndpoints.RegistrationCard>();
        if (ids.Length == 0)
        {
            return cards;
        }

        var rows = await connection.QueryAsync<(Guid Id, Guid AllocationId)>(
            "SELECT id, allocation_id FROM allocation_registrations WHERE id = ANY(@ids) AND user_id = @me", new { ids, me });
        foreach (var (id, allocationId) in rows)
        {
            if (await AllocationEndpoints.LoadRegistrationCardAsync(connection, me, allocationId, configuration) is { } card)
            {
                cards[id] = card;
            }
        }

        return cards;
    }

    private static async Task<Dictionary<Guid, FeedPromptAnswer>> LoadPromptAnswersAsync(NpgsqlConnection connection, Guid[] ids)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<(Guid Id, Guid PromptId, string PromptText, DateOnly Day, string Body, string Visibility)>(
            """
            SELECT pa.id, p.id, p.text, p.day, pa.body, pa.visibility::text
            FROM prompt_answers pa JOIN prompts p ON p.id = pa.prompt_id
            WHERE pa.id = ANY(@ids) AND pa.deleted_at IS NULL AND pa.removed_at IS NULL
            """,
            new { ids });
        return rows.ToDictionary(r => r.Id, r => new FeedPromptAnswer(r.PromptId, r.PromptText, r.Day, r.Body, r.Visibility));
    }

    private static string? Excerpt(string? review) =>
        string.IsNullOrWhiteSpace(review) ? null
        : review.Length <= ReviewExcerptLength ? review
        : review[..(ReviewExcerptLength - 1)].TrimEnd() + "…";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SeedPattern();

    private sealed record CandidateRow(
        string Type, Guid Id, DateTime At, Guid? UserId, Guid? ArtistId, string? StatKind, decimal? StatValue, decimal? StatPercentile,
        int? StatRank, int? StatFans, bool? StatIsEstimated);

    private sealed record UserRow(Guid Id, string Handle, string? DisplayName, string? AvatarUrl, bool Following);

    private sealed record PhotoRow(
        Guid Id, string Kind, string? Caption, string? ConcertName, string? ConcertVenue, string? ConcertCity, DateOnly? ConcertDate);

    private sealed record LogRow(
        Guid Id, Guid AlbumId, string AlbumTitle, string? CoverUrl, short? Rating, string? Review, bool IsRelisten, DateOnly ListenedOn,
        string Visibility);
}
