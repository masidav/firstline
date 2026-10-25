using System.Globalization;
using Dapper;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.AlbumJournal;

// openapi schemas for the album journal (tags Albums and Journal). Album rows come from the Spotify catalog
// (ADR 0006): delisted albums keep their title for existing logs but lose their cover art and Spotify link.

/// <summary>openapi UserRef. A deleted account has no handle and renders as "Deleted user".</summary>
public sealed record UserRef(string? Handle, bool Deleted, string? DisplayName, string? AvatarUrl);

public sealed record ArtistRef(Guid Id, string Name, string? ImageUrl);

public sealed record AlbumRef(Guid Id, string Title, string? CoverUrl, ArtistRef Artist, string? SpotifyAlbumId, string AlbumType);

public sealed record AlbumCommunity(int LogCount, decimal? AverageRating);

public record Album(
    Guid Id, ArtistRef Artist, string Title, DateOnly? ReleaseDate, string ReleaseDatePrecision, string? CoverUrl,
    string? SpotifyAlbumId, string AlbumType, bool Delisted, AlbumCommunity Community);

public sealed record Track(Guid Id, string Title, int? TrackNumber, int DiscNumber, string? SpotifyTrackId);

public sealed record AlbumDetail(
    Guid Id, ArtistRef Artist, string Title, DateOnly? ReleaseDate, string ReleaseDatePrecision, string? CoverUrl,
    string? SpotifyAlbumId, string AlbumType, bool Delisted, AlbumCommunity Community, IReadOnlyList<Track> Tracks,
    AlbumLog? MyLatestLog)
    : Album(Id, Artist, Title, ReleaseDate, ReleaseDatePrecision, CoverUrl, SpotifyAlbumId, AlbumType, Delisted, Community);

public sealed record AlbumPage(IReadOnlyList<Album> Items, string? NextCursor);

public sealed record AlbumLog(
    Guid Id, UserRef User, AlbumRef Album, int? Rating, string? Review, DateOnly ListenedOn, bool IsRelisten,
    string Visibility, int CommentCount, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record PointsAward(string RuleKey, decimal Points);

public sealed record LogResult(AlbumLog Log, IReadOnlyList<PointsAward> PointsAwarded);

public sealed record LogPage(IReadOnlyList<AlbumLog> Items, string? NextCursor);

/// <summary>Queries and mappings shared by the album and journal endpoints.</summary>
public static class Journal
{
    public static readonly IReadOnlyList<string> Visibilities = ["public", "followers", "private"];

    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>
    /// Which logs the viewer may see: their own, and public ones. Followers-only logs are visible to their owner
    /// alone until follows exist (Phase 8); private logs are 404 to everyone else.
    /// </summary>
    // Taken-down logs (moderation) stay visible to their author only. Followers-only logs show to the author's
    // followers (migration 0018).
    public const string VisibleToViewer =
        """
        (l.user_id = @viewer OR (l.removed_at IS NULL AND (l.visibility = 'public'
           OR (l.visibility = 'followers' AND EXISTS (SELECT 1 FROM follows fv WHERE fv.follower_id = @viewer AND fv.followee_id = l.user_id)))))
        """;

    /// <summary>Album columns with community stats (needs alias <c>a</c> for albums and <c>ar</c> for artists).</summary>
    public const string AlbumSelect =
        """
        SELECT a.id AS Id, a.title AS Title, a.release_date AS ReleaseDate, a.release_date_precision AS ReleaseDatePrecision,
               CASE WHEN a.delisted_at IS NULL THEN a.cover_url END AS CoverUrl,
               CASE WHEN a.delisted_at IS NULL THEN a.spotify_album_id END AS SpotifyAlbumId,
               a.album_type AS AlbumType, (a.delisted_at IS NOT NULL) AS Delisted,
               (SELECT count(*) FROM album_logs l WHERE l.album_id = a.id AND l.deleted_at IS NULL AND l.removed_at IS NULL)::int AS LogCount,
               (SELECT round(avg(l.rating) / 2.0, 2) FROM album_logs l
                 WHERE l.album_id = a.id AND l.deleted_at IS NULL AND l.removed_at IS NULL AND l.rating IS NOT NULL) AS AverageRating,
               ar.id AS ArtistId, ar.name AS ArtistName, ar.image_url AS ArtistImageUrl
        FROM albums a JOIN artists ar ON ar.id = a.artist_id
        """;

    /// <summary>Log columns with their owner and album (aliases <c>l</c>, <c>u</c>, <c>a</c>, <c>ar</c>).</summary>
    public const string LogSelect =
        """
        SELECT l.id AS Id, l.rating::int AS Rating, l.review AS Review, l.listened_on AS ListenedOn, l.is_relisten AS IsRelisten,
               l.visibility::text AS Visibility, l.created_at AS CreatedAt, l.updated_at AS UpdatedAt,
               (SELECT count(*) FROM comments c WHERE c.album_log_id = l.id AND c.deleted_at IS NULL)::int AS CommentCount,
               u.handle::text AS UserHandle, (u.deleted_at IS NOT NULL) AS UserDeleted, u.display_name AS UserDisplayName,
               u.avatar_url AS UserAvatarUrl, l.user_id AS UserId,
               a.id AS AlbumId, a.title AS AlbumTitle, CASE WHEN a.delisted_at IS NULL THEN a.cover_url END AS AlbumCoverUrl,
               CASE WHEN a.delisted_at IS NULL THEN a.spotify_album_id END AS AlbumSpotifyAlbumId, a.album_type AS AlbumType,
               ar.id AS ArtistId, ar.name AS ArtistName, ar.image_url AS ArtistImageUrl
        FROM album_logs l
        JOIN users u ON u.id = l.user_id
        JOIN albums a ON a.id = l.album_id
        JOIN artists ar ON ar.id = a.artist_id
        """;

    public sealed record AlbumRow(
        Guid Id, string Title, DateOnly? ReleaseDate, string ReleaseDatePrecision, string? CoverUrl, string? SpotifyAlbumId,
        string AlbumType, bool Delisted, int LogCount, decimal? AverageRating, Guid ArtistId, string ArtistName, string? ArtistImageUrl);

    public sealed record LogRow(
        Guid Id, int? Rating, string? Review, DateOnly ListenedOn, bool IsRelisten, string Visibility, DateTime CreatedAt,
        DateTime UpdatedAt, int CommentCount, string? UserHandle, bool UserDeleted, string? UserDisplayName, string? UserAvatarUrl,
        Guid UserId, Guid AlbumId, string AlbumTitle, string? AlbumCoverUrl, string? AlbumSpotifyAlbumId, string AlbumType,
        Guid ArtistId, string ArtistName, string? ArtistImageUrl);

    public static Album ToAlbum(AlbumRow r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new Album(r.Id, new ArtistRef(r.ArtistId, r.ArtistName, r.ArtistImageUrl), r.Title, r.ReleaseDate,
            r.ReleaseDatePrecision, r.CoverUrl, r.SpotifyAlbumId, r.AlbumType, r.Delisted, new AlbumCommunity(r.LogCount, r.AverageRating));
    }

    public static AlbumLog ToLog(LogRow r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new AlbumLog(r.Id,
            new UserRef(r.UserDeleted ? null : r.UserHandle, r.UserDeleted, r.UserDisplayName, r.UserAvatarUrl),
            new AlbumRef(r.AlbumId, r.AlbumTitle, r.AlbumCoverUrl, new ArtistRef(r.ArtistId, r.ArtistName, r.ArtistImageUrl),
                r.AlbumSpotifyAlbumId, r.AlbumType),
            r.Rating, r.Review, r.ListenedOn, r.IsRelisten, r.Visibility, r.CommentCount, r.CreatedAt, r.UpdatedAt);
    }

    /// <summary>Ledger rows for one source with an id above <paramref name="afterId"/>: what a write just earned.</summary>
    public static async Task<IReadOnlyList<PointsAward>> AwardsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sourceType, Guid sourceId, long afterId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return [.. await connection.QueryAsync<PointsAward>(
            """
            SELECT rule_key AS RuleKey, points AS Points FROM score_events
            WHERE source_type = @sourceType AND source_id = @sourceId AND reversed_at IS NULL AND id > @afterId
            ORDER BY id
            """,
            new { sourceType, sourceId, afterId }, transaction)];
    }

    public static Task<long> LastAwardIdAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        connection.ExecuteScalarAsync<long>("SELECT COALESCE(max(id), 0) FROM score_events", transaction: transaction);

    /// <summary>Reads <c>limit</c> and <c>cursor</c> (openapi Limit and Cursor), adding validation errors.</summary>
    public static (int Take, int Offset) ReadPage(int? limit, string? cursor, List<FieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (limit is < 1 or > MaxPageSize)
        {
            errors.Add(new FieldError("limit", $"Use a limit from 1 to {MaxPageSize}."));
        }

        var offset = 0;
        if (cursor is not null && !PageCursor.TryRead(cursor, out offset))
        {
            errors.Add(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        return (limit ?? DefaultPageSize, offset);
    }

    /// <summary>Takes one page from <paramref name="rows"/> (fetched with one extra row) and builds the next cursor.</summary>
    public static (List<T> Items, string? NextCursor) Page<T>(List<T> rows, int take, int offset)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var next = rows.Count > take ? PageCursor.Write(offset + take) : null;
        return (rows.Count > take ? rows[..take] : rows, next);
    }

    /// <summary>A LIKE pattern matching values that start with <paramref name="query"/> (escaped), case-insensitively.</summary>
    public static string PrefixPattern(string query) => Escape(query) + "%";

    /// <summary>A LIKE pattern matching values with a word that starts with <paramref name="query"/>.</summary>
    public static string WordPrefixPattern(string query) => "% " + Escape(query) + "%";

    private static string Escape(string query) =>
        query.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    public static ApiProblemException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, "not_found", $"{what} not found");

    public static ApiProblemException Forbidden(string detail) =>
        new(StatusCodes.Status403Forbidden, "forbidden", "Not allowed", detail);

    public static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
