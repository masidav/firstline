using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Concerts;

/// <summary>
/// Which concert-source artist each tracked artist is (migration 0030). The worker links an artist to Ticketmaster by
/// itself only when Ticketmaster lists the artist's Spotify page; staff set the rest here (the attraction id from a
/// Discovery API search), or remove a wrong link. Setting a link queues a sync of the artist's shows.
/// </summary>
public static partial class ConcertSourceEndpoints
{
    public static void MapConcertSources(this RouteGroupBuilder v1)
    {
        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapGet("/artists/{artistId:guid}/sources", ListAsync);
        admin.MapPut("/artists/{artistId:guid}/sources/{source}", LinkAsync);
        admin.MapDelete("/artists/{artistId:guid}/sources/{source}", UnlinkAsync);
    }

    public sealed record ArtistSource(string Source, string? ExternalId, string? MatchedBy, DateTime CheckedAt, DateTime? SyncedAt);

    public sealed record ArtistSourceList(IReadOnlyList<ArtistSource> Items);

    private const string Columns =
        "source AS Source, external_id AS ExternalId, matched_by AS MatchedBy, checked_at AS CheckedAt, synced_at AS SyncedAt";

    private static async Task<IResult> ListAsync(Guid artistId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await RequireArtistAsync(connection, artistId);
        var rows = await connection.QueryAsync<ArtistSource>(
            $"SELECT {Columns} FROM artist_sources WHERE artist_id = @artistId ORDER BY source", new { artistId });
        return Results.Ok(new ArtistSourceList([.. rows]));
    }

    private static async Task<IResult> LinkAsync(
        Guid artistId, string source, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        RequireKnownSource(source);
        var externalId = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("external_id", out var value)
                         && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
        if (externalId is null || !AttractionId().IsMatch(externalId))
        {
            throw ApiProblemException.Validation(new FieldError("external_id",
                "Send the Ticketmaster attraction id (letters and digits, like K8vZ9171ob7), not a ticketmaster.com link."));
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireArtistAsync(connection, artistId);
        var holder = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT artist_id FROM artist_sources WHERE source = @source AND external_id = @externalId AND artist_id <> @artistId",
            new { source, externalId, artistId }, transaction);
        if (holder is not null)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already linked",
                "Another tracked artist is linked to this Ticketmaster attraction.");
        }

        var row = await connection.QuerySingleAsync<ArtistSource>(
            $"""
             INSERT INTO artist_sources (artist_id, source, external_id, matched_by, checked_at)
             VALUES (@artistId, @source, @externalId, 'staff', now())
             ON CONFLICT (artist_id, source) DO UPDATE
               SET external_id = EXCLUDED.external_id, matched_by = 'staff', checked_at = now(), synced_at = NULL
             RETURNING {Columns}
             """,
            new { artistId, source, externalId }, transaction);
        await connection.ExecuteAsync(
            "SELECT enqueue_job('concerts.sync_artist', jsonb_build_object('artist_id', @artistId), 'concerts:' || @artistId)",
            new { artistId }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(row);
    }

    private static async Task<IResult> UnlinkAsync(Guid artistId, string source, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        RequireKnownSource(source);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await RequireArtistAsync(connection, artistId);
        // The shows already loaded stay (they may carry allocations); the worker may link the artist again if
        // Ticketmaster lists its Spotify page.
        await connection.ExecuteAsync("DELETE FROM artist_sources WHERE artist_id = @artistId AND source = @source", new { artistId, source });
        return Results.NoContent();
    }

    private static void RequireKnownSource(string source)
    {
        if (source != "ticketmaster")
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Unknown source",
                "Concert sources: ticketmaster.");
        }
    }

    private static async Task RequireArtistAsync(NpgsqlConnection connection, Guid artistId)
    {
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM artists WHERE id = @artistId)", new { artistId }))
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Artist not found", "No tracked artist has this id.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9]{5,40}$")]
    private static partial Regex AttractionId();
}
