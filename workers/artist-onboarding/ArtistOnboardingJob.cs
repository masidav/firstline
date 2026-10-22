using System.Text.Json;
using Dapper;
using FandomScore.Spotify;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.ArtistOnboarding;

/// <summary>
/// Takes an artist from <c>onboarding</c> to <c>active</c> (payload <c>{"artist_id": ...}</c>, queued by
/// <c>track_artist()</c> through <c>request_artist_onboarding()</c>): loads the catalog from Spotify (ADR 0006),
/// stores a provisional baseline, activates the artist, and queues a sync for the fan who added it. Other fans
/// pick the artist up on their next scheduled sync. Idempotent: an artist that is no longer onboarding is left
/// alone, and a failed attempt changes nothing except the catalog rows, which the next attempt overwrites.
/// </summary>
public sealed partial class ArtistOnboardingJob(
    NpgsqlDataSource db, CatalogLoader catalog, IOptions<SpotifyOptions> spotifyOptions, IOptions<BaselineOptions> baselineOptions,
    TimeProvider time, ILogger<ArtistOnboardingJob> logger) : IJobHandler
{
    public const string JobKind = "artist.onboard";

    public string Kind => JobKind;

    private sealed record ArtistRow(Guid Id, string SpotifyArtistId, string Status, Guid? AddedBy);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var payload = JsonDocument.Parse(job.Payload);
        var artistId = payload.RootElement.GetProperty("artist_id").GetGuid();

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var artist = await connection.QuerySingleOrDefaultAsync<ArtistRow>(
            "SELECT id AS Id, spotify_artist_id AS SpotifyArtistId, status::text AS Status, added_by AS AddedBy FROM artists WHERE id = @artistId",
            new { artistId });
        if (artist is null || artist.Status != "onboarding")
        {
            LogSkipped(logger, artistId, artist?.Status ?? "missing");
            return;
        }

        if (!spotifyOptions.Value.IsCatalogEnabled)
        {
            // Retried by the queue; the artist stays onboarding until the server is configured.
            throw new InvalidOperationException("Spotify:ClientId and Spotify:ClientSecret are needed to load catalogs.");
        }

        await using var lease = await CatalogLock.TryAcquireAsync(connection, artistId);
        if (lease is null)
        {
            throw new InvalidOperationException("Another catalog job for this artist is running.");
        }

        var load = await catalog.LoadAsync(connection, artistId, artist.SpotifyArtistId, cancellationToken);
        if (load is null)
        {
            await connection.ExecuteAsync("UPDATE artists SET status = 'paused' WHERE id = @artistId AND status = 'onboarding'", new { artistId });
            LogNotOnSpotify(logger, artistId);
            return;
        }

        var baseline = ProvisionalBaseline.Create(baselineOptions.Value, load, time.GetUtcNow());
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var activated = await connection.ExecuteScalarAsync<bool>(
            """
            INSERT INTO artist_baselines (artist_id, dist_family, dist_params, proxy_metrics, curve, blend_k)
            VALUES (@artistId, 'lognormal', @distParams::jsonb, @proxyMetrics::jsonb, @curve::jsonb, @blendK)
            ON CONFLICT (artist_id) DO NOTHING;   -- a baseline set by hand wins

            WITH activated AS (
              UPDATE artists SET status = 'active' WHERE id = @artistId AND status = 'onboarding' RETURNING added_by
            )
            SELECT EXISTS (SELECT 1 FROM activated);
            """,
            new { artistId, baseline.DistParams, baseline.ProxyMetrics, baseline.Curve, baseline.BlendK }, transaction);
        if (activated && artist.AddedBy is { } addedBy)
        {
            // The fan who added the artist sees their score first; no-op without a Spotify connection.
            await connection.ExecuteAsync("SELECT request_spotify_sync(@addedBy)", new { addedBy }, transaction);
        }

        await transaction.CommitAsync(cancellationToken);
        LogActivated(logger, artistId, load.Albums, load.Tracks);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Artist {ArtistId} activated with {Albums} releases and {Tracks} tracks.")]
    private static partial void LogActivated(ILogger logger, Guid artistId, int albums, int tracks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Artist {ArtistId} paused: Spotify has no artist with its id.")]
    private static partial void LogNotOnSpotify(ILogger logger, Guid artistId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Onboarding skipped for artist {ArtistId} (status {Status}).")]
    private static partial void LogSkipped(ILogger logger, Guid artistId, string status);
}

/// <summary>Session-level advisory lock so onboarding and refresh never load one artist's catalog at the same time.</summary>
internal sealed class CatalogLock(NpgsqlConnection connection, string key) : IAsyncDisposable
{
    public static async Task<CatalogLock?> TryAcquireAsync(NpgsqlConnection connection, Guid artistId)
    {
        var key = $"catalog:{artistId}";
        var acquired = await connection.ExecuteScalarAsync<bool>(
            "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", new { key });
        return acquired ? new CatalogLock(connection, key) : null;
    }

    public async ValueTask DisposeAsync() =>
        await connection.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", new { key });
}
