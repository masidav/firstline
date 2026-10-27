using System.Globalization;
using System.Text.Json;
using Dapper;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.Concerts;

/// <summary>
/// <c>concerts.sync_artist</c> (payload <c>{"artist_id": ...}</c>): loads an active artist's upcoming Ticketmaster shows
/// into <c>events</c> (migration 0030). Queued by <see cref="ScheduleConcertSyncsJob"/> (new artists first) and when staff
/// link an artist.
/// <list type="number">
/// <item>Link: an artist without a Ticketmaster id is searched by name, and linked only to an attraction that lists the
/// artist's Spotify page (no guessing by name; staff link the rest). No match is recorded and retried weekly.</item>
/// <item>Load: every upcoming show in <see cref="TicketmasterOptions.Countries"/> is inserted or updated by its
/// Ticketmaster id, with its link, status, public sale, and presales. A show with an allocation keeps the time and
/// city staff locked; the source's time goes to <c>source_starts_at</c>.</item>
/// </list>
/// Idempotent. Staff-entered shows are never touched. Shows Ticketmaster stops listing are left as they are.
/// </summary>
public sealed partial class ConcertSyncJob(
    NpgsqlDataSource db, TicketmasterClient ticketmaster, IOptions<TicketmasterOptions> options, TimeProvider time,
    ILogger<ConcertSyncJob> logger) : IJobHandler
{
    public const string JobKind = "concerts.sync_artist";
    public const string Source = "ticketmaster";

    private readonly TicketmasterOptions _options = options.Value;

    public string Kind => JobKind;

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!ticketmaster.IsEnabled)
        {
            LogDisabled(logger);
            return;
        }

        using var payload = JsonDocument.Parse(job.Payload);
        if (!payload.RootElement.TryGetProperty("artist_id", out var idElement) || !idElement.TryGetGuid(out var artistId))
        {
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var artist = await connection.QuerySingleOrDefaultAsync<ArtistRow>(
            "SELECT name AS Name, spotify_artist_id AS SpotifyArtistId FROM artists WHERE id = @artistId AND status = 'active'",
            new { artistId });
        if (artist is null)
        {
            return;   // not tracked (any more)
        }

        var link = await connection.QuerySingleOrDefaultAsync<LinkRow>(
            "SELECT external_id AS ExternalId, checked_at AS CheckedAt, synced_at AS SyncedAt FROM artist_sources WHERE artist_id = @artistId AND source = @Source",
            new { artistId, Source });
        var attractionId = link?.ExternalId;
        var now = time.GetUtcNow();
        if (attractionId is null && (link is null || link.CheckedAt < now - _options.RecheckUnmatchedEvery))
        {
            attractionId = await MatchAsync(connection, artistId, artist, cancellationToken);
        }

        if (attractionId is null)
        {
            return;
        }

        // The first load after linking also fills the artist's website from Ticketmaster's homepage link, if staff
        // haven't set one (migration 0038). Later syncs don't spend a call on it.
        if (link?.SyncedAt is null)
        {
            await FillWebsiteAsync(connection, artistId, attractionId, cancellationToken);
        }

        var countries = _options.CountrySet;
        var events = (await ticketmaster.GetUpcomingEventsAsync(attractionId, now, cancellationToken))
            .Where(e => e.Country is { } c && countries.Contains(c.ToUpperInvariant()))
            .ToList();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var show in events)
        {
            await UpsertAsync(connection, transaction, artistId, show);
        }

        await connection.ExecuteAsync(
            "UPDATE artist_sources SET synced_at = now() WHERE artist_id = @artistId AND source = @Source",
            new { artistId, Source }, transaction);
        await transaction.CommitAsync(cancellationToken);
        LogSynced(logger, artist.Name, events.Count);
    }

    /// <summary>Links the artist to the attraction that lists its Spotify page, or records that none does.</summary>
    private async Task<string?> MatchAsync(NpgsqlConnection connection, Guid artistId, ArtistRow artist, CancellationToken cancellationToken)
    {
        var candidates = await ticketmaster.SearchAttractionsAsync(artist.Name, cancellationToken);
        var match = candidates.FirstOrDefault(a => a.Id.Length > 0 && a.SpotifyArtistIds.Contains(artist.SpotifyArtistId, StringComparer.Ordinal));
        // Another artist can't already hold this attraction (Spotify ids are unique), but a staff link might: keep it.
        var linked = await connection.ExecuteScalarAsync<string?>(
            """
            INSERT INTO artist_sources AS s (artist_id, source, external_id, matched_by, checked_at)
            SELECT @artistId, @Source, @externalId, CASE WHEN @externalId IS NULL THEN NULL ELSE 'spotify_link' END, now()
            WHERE @externalId IS NULL
               OR NOT EXISTS (SELECT 1 FROM artist_sources WHERE source = @Source AND external_id = @externalId)
            ON CONFLICT (artist_id, source) DO UPDATE
              SET external_id = COALESCE(s.external_id, EXCLUDED.external_id),
                  matched_by = COALESCE(s.matched_by, EXCLUDED.matched_by),
                  checked_at = now()
            RETURNING external_id
            """,
            new { artistId, Source, externalId = match?.Id });
        if (linked is not null)
        {
            LogLinked(logger, artist.Name, linked);
        }

        return linked;
    }

    private async Task FillWebsiteAsync(NpgsqlConnection connection, Guid artistId, string attractionId, CancellationToken cancellationToken)
    {
        if (await connection.ExecuteScalarAsync<bool>("SELECT website_url IS NOT NULL FROM artists WHERE id = @artistId", new { artistId }))
        {
            return;
        }

        if ((await ticketmaster.GetAttractionAsync(attractionId, cancellationToken))?.Homepage is { } homepage)
        {
            await connection.ExecuteAsync(
                "UPDATE artists SET website_url = @homepage WHERE id = @artistId AND website_url IS NULL", new { artistId, homepage });
        }
    }

    private static async Task UpsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid artistId, TicketmasterEvent show)
    {
        var eventId = await connection.ExecuteScalarAsync<Guid>(
            """
            WITH v AS (
              SELECT COALESCE(@startsAt::timestamptz,
                              (@localDate::date + COALESCE(@localTime::time, time '00:00'))
                                AT TIME ZONE (CASE WHEN EXISTS (SELECT 1 FROM pg_timezone_names WHERE name = @timezone)
                                                   THEN @timezone ELSE 'UTC' END)) AS starts_at,
                     hub_city_for(@city, @region, @country) AS city_id)
            INSERT INTO events AS e (artist_id, name, venue, city_id, city, region, country, starts_at, external_source,
                                     external_id, ticket_url, status, time_tba, public_sale_starts_at, public_sale_ends_at,
                                     source_starts_at, source_synced_at)
            SELECT @artistId, @name, @venue, v.city_id, @city, @region, @country, v.starts_at, 'ticketmaster',
                   @externalId, @url, @status, @timeTba, @publicStart, @publicEnd, v.starts_at, now()
            FROM v
            ON CONFLICT (external_source, external_id) DO UPDATE SET
              name = EXCLUDED.name, venue = EXCLUDED.venue, city = EXCLUDED.city, region = EXCLUDED.region,
              country = EXCLUDED.country, ticket_url = EXCLUDED.ticket_url, status = EXCLUDED.status,
              time_tba = EXCLUDED.time_tba, public_sale_starts_at = EXCLUDED.public_sale_starts_at,
              public_sale_ends_at = EXCLUDED.public_sale_ends_at, source_starts_at = EXCLUDED.source_starts_at,
              source_synced_at = now(),
              -- An allocation fixes the time and city staff set (trg_event_lock); the source's time is kept beside it.
              starts_at = CASE WHEN EXISTS (SELECT 1 FROM allocations a WHERE a.event_id = e.id AND a.cancelled_at IS NULL)
                               THEN e.starts_at ELSE EXCLUDED.starts_at END,
              city_id = CASE WHEN EXISTS (SELECT 1 FROM allocations a WHERE a.event_id = e.id AND a.cancelled_at IS NULL)
                             THEN e.city_id ELSE EXCLUDED.city_id END
            WHERE e.artist_id = @artistId
            RETURNING e.id
            """,
            new
            {
                artistId, name = Trim(show.Name, 200) ?? "Live", venue = Trim(show.Venue, 200), city = Trim(show.City, 100),
                region = Trim(show.Region, 10)?.ToUpperInvariant(), country = show.Country?.ToUpperInvariant(),
                startsAt = show.StartsAt, localDate = show.LocalDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                localTime = show.LocalTime?.ToString("HH:mm:ss", CultureInfo.InvariantCulture), timezone = show.Timezone ?? "UTC",
                externalId = show.Id, url = show.Url, status = show.Status, timeTba = show.TimeTba,
                publicStart = show.PublicSaleStartsAt, publicEnd = show.PublicSaleEndsAt,
            },
            transaction);
        if (eventId == Guid.Empty)
        {
            return;   // this Ticketmaster id belongs to a show of another artist (a co-headline): left to that one
        }

        await connection.ExecuteAsync("DELETE FROM event_presales WHERE event_id = @eventId", new { eventId }, transaction);
        foreach (var presale in show.Presales)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO event_presales (event_id, name, starts_at, ends_at) VALUES (@eventId, @name, @startsAt, @endsAt)
                ON CONFLICT DO NOTHING
                """,
                new { eventId, name = Trim(presale.Name, 200), startsAt = presale.StartsAt, endsAt = presale.EndsAt }, transaction);
        }
    }

    private static string? Trim(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim() is { Length: var n } t && n > max ? t[..max] : text.Trim();

    private sealed record ArtistRow(string Name, string SpotifyArtistId);

    private sealed record LinkRow(string? ExternalId, DateTime CheckedAt, DateTime? SyncedAt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ticketmaster is not configured (Ticketmaster__ApiKey); skipping the concert sync.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Linked {Artist} to Ticketmaster attraction {AttractionId} (Spotify link).")]
    private static partial void LogLinked(ILogger logger, string artist, string attractionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} upcoming Ticketmaster shows for {Artist}.")]
    private static partial void LogSynced(ILogger logger, string artist, int count);
}

/// <summary>
/// Hourly <c>concerts.schedule</c>: queues <see cref="ConcertSyncJob"/> for active artists whose Ticketmaster shows are
/// older than <see cref="TicketmasterOptions.SyncEvery"/>, never looked up, or unmatched for longer than
/// <see cref="TicketmasterOptions.RecheckUnmatchedEvery"/>, at most <see cref="TicketmasterOptions.MaxSyncsPerHour"/>
/// a run (oldest first) to stay inside the daily quota.
/// </summary>
public sealed partial class ScheduleConcertSyncsJob(
    NpgsqlDataSource db, IOptions<TicketmasterOptions> options, ILogger<ScheduleConcertSyncsJob> logger) : IJobHandler
{
    public const string JobKind = "concerts.schedule";

    private readonly TicketmasterOptions _options = options.Value;

    public string Kind => JobKind;

    public TimeSpan? Every => TimeSpan.FromHours(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!_options.IsEnabled)
        {
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var queued = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(enqueue_job(@kind, jsonb_build_object('artist_id', due.id), 'concerts:' || due.id))
            FROM (SELECT a.id
                  FROM artists a LEFT JOIN artist_sources s ON s.artist_id = a.id AND s.source = 'ticketmaster'
                  WHERE a.status = 'active'
                    AND (s.artist_id IS NULL
                         OR (s.external_id IS NOT NULL AND (s.synced_at IS NULL OR s.synced_at < now() - @syncEvery))
                         OR (s.external_id IS NULL AND s.checked_at < now() - @recheck))
                  ORDER BY COALESCE(s.synced_at, s.checked_at) NULLS FIRST, a.id
                  LIMIT @max) due
            """,
            new { kind = ConcertSyncJob.JobKind, syncEvery = _options.SyncEvery, recheck = _options.RecheckUnmatchedEvery, max = _options.MaxSyncsPerHour });
        if (queued > 0)
        {
            LogQueued(logger, queued);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued {Count} Ticketmaster concert syncs.")]
    private static partial void LogQueued(ILogger logger, int count);
}
