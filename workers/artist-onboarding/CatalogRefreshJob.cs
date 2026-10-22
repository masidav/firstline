using System.Text.Json;
using Dapper;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.ArtistOnboarding;

/// <summary>
/// Reloads an active artist's catalog from Spotify (payload <c>{"artist_id": ...}</c>), queued by
/// <see cref="ScheduleCatalogRefreshesJob"/>. Keeps titles, cover URLs, and track lists current and delists
/// releases Spotify dropped (ADR 0006). An artist Spotify no longer has is paused.
/// </summary>
public sealed partial class CatalogRefreshJob(
    NpgsqlDataSource db, CatalogLoader catalog, IOptions<SpotifyOptions> options, ILogger<CatalogRefreshJob> logger) : IJobHandler
{
    public const string JobKind = "catalog.refresh";

    public string Kind => JobKind;

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var payload = JsonDocument.Parse(job.Payload);
        var artistId = payload.RootElement.GetProperty("artist_id").GetGuid();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var spotifyArtistId = await connection.ExecuteScalarAsync<string?>(
            "SELECT spotify_artist_id FROM artists WHERE id = @artistId AND status = 'active'", new { artistId });
        if (spotifyArtistId is null)
        {
            return;
        }

        if (!options.Value.IsCatalogEnabled)
        {
            throw new InvalidOperationException("Spotify:ClientId and Spotify:ClientSecret are needed to load catalogs.");
        }

        await using var lease = await CatalogLock.TryAcquireAsync(connection, artistId);
        if (lease is null)
        {
            throw new InvalidOperationException("Another catalog job for this artist is running.");
        }

        if (await catalog.LoadAsync(connection, artistId, spotifyArtistId, cancellationToken) is null)
        {
            await connection.ExecuteAsync("UPDATE artists SET status = 'paused' WHERE id = @artistId AND status = 'active'", new { artistId });
            LogNotOnSpotify(logger, artistId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Artist {ArtistId} paused: Spotify no longer has an artist with its id.")]
    private static partial void LogNotOnSpotify(ILogger logger, Guid artistId);
}

/// <summary>
/// Hourly: queues a <c>catalog.refresh</c> for each active artist whose catalog is older than
/// <see cref="SpotifyOptions.CatalogRefreshEvery"/>, oldest first, a batch at a time so the refreshes spread out.
/// </summary>
public sealed partial class ScheduleCatalogRefreshesJob(
    NpgsqlDataSource db, IOptions<SpotifyOptions> options, ILogger<ScheduleCatalogRefreshesJob> logger) : IJobHandler
{
    private const int BatchSize = 200;

    public string Kind => "catalog.schedule_refreshes";

    public TimeSpan? Every => TimeSpan.FromHours(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!options.Value.IsCatalogEnabled)
        {
            return;
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var queued = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(enqueue_job(@kind, jsonb_build_object('artist_id', id), @kind || ':' || id))::integer
            FROM (SELECT id FROM artists
                  WHERE status = 'active' AND COALESCE(catalog_loaded_at, '-infinity') < now() - @every
                  ORDER BY catalog_loaded_at NULLS FIRST
                  LIMIT @BatchSize) due
            """,
            new { kind = CatalogRefreshJob.JobKind, every = options.Value.CatalogRefreshEvery, BatchSize });
        LogQueued(logger, queued);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued {Count} catalog refreshes.")]
    private static partial void LogQueued(ILogger logger, int count);
}
