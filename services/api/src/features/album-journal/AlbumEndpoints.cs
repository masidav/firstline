using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Artists;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using FandomScore.Spotify;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Api.Features.AlbumJournal;

/// <summary>
/// openapi tag Albums: an artist's albums, album search, album detail, and an album's community logs. Everything
/// reads the local catalog (ADR 0006); Spotify is only asked for artist names when a search finds nothing here.
/// </summary>
public static partial class AlbumEndpoints
{
    private const int UntrackedArtistSuggestions = 5;

    public static void MapAlbums(this RouteGroupBuilder v1)
    {
        v1.MapGet("/artists/{artistId:guid}/albums", ListByArtistAsync);
        v1.MapGet("/albums/search", SearchAsync).RequireRateLimiting(Artists.ArtistEndpoints.SearchRateLimit);   // falls through to Spotify search
        v1.MapGet("/albums/{albumId:guid}", GetAsync);
        v1.MapGet("/albums/{albumId:guid}/logs", ListLogsAsync);
    }

    public sealed record AlbumSearchResults(IReadOnlyList<Album> Items, IReadOnlyList<ArtistEndpoints.ArtistSearchResult> UntrackedArtists);

    // Live albums only, newest first. Delisted albums stay reachable by id for the logs that mention them.
    private static async Task<IResult> ListByArtistAsync(
        Guid artistId, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var (take, offset) = Journal.ReadPage(limit, cursor, errors);
        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM artists WHERE id = @artistId)", new { artistId }))
        {
            throw Journal.NotFound("Artist");
        }

        var rows = (await connection.QueryAsync<Journal.AlbumRow>(
            $"""
             {Journal.AlbumSelect}
             WHERE a.artist_id = @artistId AND a.delisted_at IS NULL
             ORDER BY a.release_date DESC NULLS LAST, lower(a.title), a.id
             LIMIT @take OFFSET @offset
             """,
            new { artistId, take = take + 1, offset })).ToList();
        var (items, next) = Journal.Page(rows, take, offset);
        return Results.Ok(new AlbumPage([.. items.Select(Journal.ToAlbum)], next));
    }

    // Matches album titles and artist names by prefix (or word prefix) in the live catalog of unpaused artists.
    // Only when nothing matches is Spotify asked for artists with that name, so the app can offer "add this
    // artist first"; artists already tracked are left out of that list.
    private static async Task<IResult> SearchAsync(
        string? q, Guid? artistId, int? limit, NpgsqlDataSource db, SpotifyClient spotify, IOptions<SpotifyOptions> options,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        var errors = new List<FieldError>();
        if (query.Length is < 2 or > 100)
        {
            errors.Add(new FieldError("q", "Send 2 to 100 characters."));
        }

        var (take, _) = Journal.ReadPage(limit, null, errors);
        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Journal.AlbumRow>(
            $"""
             {Journal.AlbumSelect}
             WHERE a.delisted_at IS NULL AND ar.status <> 'paused'
               AND (@artistId::uuid IS NULL OR a.artist_id = @artistId::uuid)
               AND (a.title ILIKE @prefix OR a.title ILIKE @wordPrefix OR ar.name ILIKE @prefix OR ar.name ILIKE @wordPrefix)
             ORDER BY (a.title ILIKE @prefix) DESC, a.release_date DESC NULLS LAST, lower(a.title), a.id
             LIMIT @take
             """,
            new { artistId, prefix = Journal.PrefixPattern(query), wordPrefix = Journal.WordPrefixPattern(query), take })).ToList();

        var untracked = new List<ArtistEndpoints.ArtistSearchResult>();
        if (rows.Count == 0 && artistId is null && options.Value.IsCatalogEnabled)
        {
            try
            {
                var found = await spotify.SearchArtistsAsync(query, UntrackedArtistSuggestions, cancellationToken);
                var tracked = (await connection.QueryAsync<string>(
                        "SELECT spotify_artist_id FROM artists WHERE spotify_artist_id = ANY(@ids)",
                        new { ids = found.Select(a => a.Id).ToArray() }))
                    .ToHashSet(StringComparer.Ordinal);
                untracked.AddRange(found.Where(a => !tracked.Contains(a.Id)).Select(a =>
                    new ArtistEndpoints.ArtistSearchResult(a.Id, a.Name, a.ImageUrl, a.Genres, false, null, null, null)));
            }
            catch (Exception e) when (e is SpotifyUnavailableException or SpotifyRequestException or SpotifyAuthorizationException)
            {
                // The local results still stand; the suggestion is best effort.
                LogSpotifyUnavailable(loggers.CreateLogger(typeof(AlbumEndpoints)), e);
            }
        }

        return Results.Ok(new AlbumSearchResults([.. rows.Select(Journal.ToAlbum)], untracked));
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, Guid albumId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Journal.AlbumRow>(
            $"{Journal.AlbumSelect} WHERE a.id = @albumId", new { albumId })
            ?? throw Journal.NotFound("Album");
        var tracks = await connection.QueryAsync<Track>(
            """
            SELECT id AS Id, title AS Title, track_number::int AS TrackNumber, disc_number::int AS DiscNumber, spotify_track_id AS SpotifyTrackId
            FROM tracks WHERE album_id = @albumId AND delisted_at IS NULL
            ORDER BY disc_number, track_number NULLS LAST, title
            """,
            new { albumId });
        var latest = await connection.QuerySingleOrDefaultAsync<Journal.LogRow>(
            $"""
             {Journal.LogSelect}
             WHERE l.user_id = @viewer AND l.album_id = @albumId AND l.deleted_at IS NULL
             ORDER BY l.created_at DESC LIMIT 1
             """,
            new { viewer = user.UserId(), albumId });

        var album = Journal.ToAlbum(row);
        return Results.Ok(new AlbumDetail(album.Id, album.Artist, album.Title, album.ReleaseDate, album.ReleaseDatePrecision,
            album.CoverUrl, album.SpotifyAlbumId, album.AlbumType, album.Delisted, album.Community, [.. tracks],
            latest is null ? null : Journal.ToLog(latest)));
    }

    // Logs visible to the caller: public ones and their own.
    private static async Task<IResult> ListLogsAsync(
        ClaimsPrincipal user, Guid albumId, string? sort, bool? withReview, int? limit, string? cursor, NpgsqlDataSource db,
        CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var (take, offset) = Journal.ReadPage(limit, cursor, errors);
        var orderBy = (sort ?? "recent") switch
        {
            "recent" => "l.created_at DESC, l.id",
            "discussed" => "CommentCount DESC, l.created_at DESC, l.id",
            _ => null,
        };
        if (orderBy is null)
        {
            errors.Add(new FieldError("sort", "Use recent or discussed."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM albums WHERE id = @albumId)", new { albumId }))
        {
            throw Journal.NotFound("Album");
        }

        var rows = (await connection.QueryAsync<Journal.LogRow>(
            $"""
             {Journal.LogSelect}
             WHERE l.album_id = @albumId AND l.deleted_at IS NULL AND {Journal.VisibleToViewer}
               AND (NOT @withReview OR (l.review IS NOT NULL AND l.review <> ''))
             ORDER BY {orderBy}
             LIMIT @take OFFSET @offset
             """,
            new { albumId, viewer = user.UserId(), withReview = withReview ?? false, take = take + 1, offset })).ToList();
        var (items, next) = Journal.Page(rows, take, offset);
        return Results.Ok(new LogPage([.. items.Select(Journal.ToLog)], next));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Spotify artist suggestions for album search unavailable.")]
    private static partial void LogSpotifyUnavailable(ILogger logger, Exception exception);
}
