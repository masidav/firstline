using System.Security.Claims;
using System.Text.RegularExpressions;
using Dapper;
using FandomScore.Api.Features.Artists;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Follows;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using FandomScore.Spotify;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Api.Features.GrabBag;

/// <summary>
/// openapi tag <c>Social</c>, grab bags (migration 0018, decided 2026-10-20): up to five favourite songs per fan in
/// numbered slots, shared with followers (can't be turned off), each linking to Spotify. Songs are looked up on
/// Spotify with the app token; the song's primary artist is tracked through <c>track_artist(..., 'grab_bag')</c>,
/// which doesn't count toward the daily artist limit.
/// </summary>
public static partial class GrabBagEndpoints
{
    public const int Slots = 5;

    public static void MapGrabBag(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/grab-bag", GetMineAsync);
        v1.MapPut("/me/grab-bag/{position:int}", PutAsync);
        v1.MapDelete("/me/grab-bag/{position:int}", DeleteAsync);
        v1.MapGet("/users/{handle}/grab-bag", GetForUserAsync);
        v1.MapGet("/me/bouncer-door", BouncerDoorAsync);
        v1.MapGet("/tracks/search", SearchTracksAsync).RequireRateLimiting(ArtistEndpoints.SearchRateLimit);
    }

    /// <summary>openapi GrabBagSong. <c>ArtistStatus</c> tells the app whether the artist is still onboarding.</summary>
    public sealed record GrabBagSong(
        Guid Id, int Position, string SpotifyTrackId, string Title, ScoringEndpoints.ArtistRef Artist, string ArtistStatus,
        string? AlbumTitle, string? CoverUrl, string SpotifyUrl, DateTime AddedAt);

    public sealed record GrabBag(SubmissionEndpoints.UserRef Owner, IReadOnlyList<GrabBagSong> Items, int Slots);

    public sealed record PutSongRequest(string? SpotifyTrackId);

    public sealed record TrackArtist(string SpotifyArtistId, string Name, Guid? ArtistId);

    public sealed record TrackSearchResult(
        string SpotifyTrackId, string Title, IReadOnlyList<TrackArtist> Artists, string? AlbumTitle, string? CoverUrl, string SpotifyUrl);

    public sealed record TrackSearchResults(IReadOnlyList<TrackSearchResult> Items);

    public static string SpotifyUrl(string spotifyTrackId) => $"https://open.spotify.com/track/{spotifyTrackId}";

    /// <summary>
    /// openapi BouncerDoor: one song drawn at random from the grab bags of the fans the caller follows, with whose it
    /// is. <c>Song</c> is null when the caller follows nobody (<c>Following</c> 0) or nobody they follow has filled
    /// their bag (<c>Songs</c> 0).
    /// </summary>
    public sealed record BouncerDoor(GrabBagSong? Song, SubmissionEndpoints.UserRef? Owner, int Following, int Songs);

    // The bouncer door (decided 2026-10-20): a recommended real track from the taste of someone the fan follows. A
    // fresh draw on every call; the owner's name is shown with the song.
    private static async Task<IResult> BouncerDoorAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var counts = await connection.QuerySingleAsync<(int Following, int Songs)>(
            """
            SELECT (SELECT count(*) FROM follows f JOIN users u ON u.id = f.followee_id AND u.deleted_at IS NULL WHERE f.follower_id = @me)::int,
                   (SELECT count(*) FROM grab_bag_songs g JOIN follows f ON f.followee_id = g.user_id AND f.follower_id = @me
                      JOIN artists a ON a.id = g.artist_id AND a.status IN ('onboarding', 'active'))::int
            """,
            new { me });
        var row = await connection.QuerySingleOrDefaultAsync<SongRow>(
            $"""
             SELECT {SongColumns} FROM grab_bag_songs g JOIN artists a ON a.id = g.artist_id AND a.status IN ('onboarding', 'active')
             JOIN follows f ON f.followee_id = g.user_id AND f.follower_id = @me
             JOIN users u ON u.id = g.user_id AND u.deleted_at IS NULL
             ORDER BY random() LIMIT 1
             """,
            new { me });
        if (row is null)
        {
            return Results.Ok(new BouncerDoor(null, null, counts.Following, counts.Songs));
        }

        var owner = await connection.QuerySingleAsync<SubmissionEndpoints.UserRef>(
            "SELECT u.handle::text AS Handle, false AS Deleted, u.display_name AS DisplayName, u.avatar_url AS AvatarUrl FROM grab_bag_songs g JOIN users u ON u.id = g.user_id WHERE g.id = @id",
            new { id = row.Id });
        return Results.Ok(new BouncerDoor(ToSong(row), owner, counts.Following, counts.Songs));
    }

    private static async Task<IResult> GetMineAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return Results.Ok(await LoadAsync(connection, user.UserId()));
    }

    // Owner and followers only (shared with followers by default, and that can't be turned off).
    private static async Task<IResult> GetForUserAsync(
        ClaimsPrincipal user, string handle, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var owner = await FollowEndpoints.LiveUserAsync(connection, handle);
        if (owner != me && !await FollowEndpoints.IsFollowingAsync(connection, me, owner))
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "followers_only", "Followers only",
                "Grab bags are shared with followers. Follow this fan to see theirs.");
        }

        return Results.Ok(await LoadAsync(connection, owner));
    }

    // Puts a song in a slot (replacing what was there). A song already in another slot moves to this one.
    private static async Task<IResult> PutAsync(
        ClaimsPrincipal user, int position, PutSongRequest? request, NpgsqlDataSource db, SpotifyClient spotify,
        IOptions<SpotifyOptions> options, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        if (position is < 1 or > Slots)
        {
            errors.Add(new FieldError("position", $"Use a slot from 1 to {Slots}."));
        }

        if (request?.SpotifyTrackId is not { } spotifyTrackId || !SpotifyIdPattern().IsMatch(spotifyTrackId))
        {
            errors.Add(new FieldError("spotify_track_id", "Send the 22-character Spotify track id from search."));
            spotifyTrackId = "";
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        ArtistEndpoints.RequireCatalog(options.Value, "Grab bags are not set up on this server.");
        var track = await ArtistEndpoints.CallSpotifyAsync(() => spotify.GetTrackAsync(spotifyTrackId, cancellationToken), loggers)
                    ?? throw new ApiProblemException(StatusCodes.Status404NotFound, "spotify_track_not_found", "Song not found",
                        "Spotify has no track with this id.");
        var primary = track.Artists[0];

        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var artistId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT o_artist_id FROM track_artist(@me, @spotifyArtistId, @name, 50, 'grab_bag')",
            new { me, spotifyArtistId = primary.Id, name = primary.Name }, transaction);
        await connection.ExecuteAsync(
            """
            DELETE FROM grab_bag_songs WHERE user_id = @me AND spotify_track_id = @spotifyTrackId AND position <> @position;
            INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id, album_title, cover_url)
            VALUES (@me, @position, @spotifyTrackId, @title, @artistId, @albumTitle, @coverUrl)
            ON CONFLICT (user_id, position) DO UPDATE
              SET spotify_track_id = EXCLUDED.spotify_track_id, title = EXCLUDED.title, artist_id = EXCLUDED.artist_id,
                  album_title = EXCLUDED.album_title, cover_url = EXCLUDED.cover_url, added_at = now()
            """,
            new { me, position, spotifyTrackId, title = track.Name, artistId, albumTitle = track.AlbumName, coverUrl = track.CoverUrl },
            transaction);
        var bag = await LoadAsync(connection, me, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(bag);
    }

    private static async Task<IResult> DeleteAsync(ClaimsPrincipal user, int position, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (position is < 1 or > Slots)
        {
            throw ApiProblemException.Validation(new FieldError("position", $"Use a slot from 1 to {Slots}."));
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM grab_bag_songs WHERE user_id = @me AND position = @position",
            new { me = user.UserId(), position });
        return Results.NoContent();
    }

    private static async Task<IResult> SearchTracksAsync(
        string? q, int? limit, NpgsqlDataSource db, SpotifyClient spotify, IOptions<SpotifyOptions> options,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        var errors = new List<FieldError>();
        if (query.Length is < 2 or > 100)
        {
            errors.Add(new FieldError("q", "Send 2 to 100 characters to search for."));
        }

        if (limit is < 1 or > SpotifyClient.MaxSearchLimit)
        {
            errors.Add(new FieldError("limit", $"Use a limit from 1 to {SpotifyClient.MaxSearchLimit}."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        ArtistEndpoints.RequireCatalog(options.Value, "Song search is not set up on this server.");
        var found = await ArtistEndpoints.CallSpotifyAsync(
            () => spotify.SearchTracksAsync(query, limit ?? SpotifyClient.MaxSearchLimit, cancellationToken), loggers);

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var tracked = (await connection.QueryAsync<(string SpotifyArtistId, Guid Id)>(
                "SELECT spotify_artist_id, id FROM artists WHERE spotify_artist_id = ANY(@ids)",
                new { ids = found.SelectMany(t => t.Artists.Select(a => a.Id)).Distinct().ToArray() }))
            .ToDictionary(row => row.SpotifyArtistId, row => row.Id);
        return Results.Ok(new TrackSearchResults([.. found.Select(track => new TrackSearchResult(
            track.Id, track.Name,
            [.. track.Artists.Select(a => new TrackArtist(a.Id, a.Name, tracked.TryGetValue(a.Id, out var id) ? id : null))],
            track.AlbumName, track.CoverUrl, track.SpotifyUrl))]));
    }

    /// <summary>A fan's grab bag, slots in order.</summary>
    public static async Task<GrabBag> LoadAsync(NpgsqlConnection connection, Guid ownerId, NpgsqlTransaction? transaction = null)
    {
        var owner = await connection.QuerySingleAsync<SubmissionEndpoints.UserRef>(
            "SELECT handle::text AS Handle, deleted_at IS NOT NULL AS Deleted, display_name AS DisplayName, avatar_url AS AvatarUrl FROM users WHERE id = @ownerId",
            new { ownerId }, transaction);
        var songs = await connection.QueryAsync<SongRow>(
            $"""
             SELECT {SongColumns} FROM grab_bag_songs g JOIN artists a ON a.id = g.artist_id
             WHERE g.user_id = @ownerId ORDER BY g.position
             """,
            new { ownerId }, transaction);
        return new GrabBag(owner, [.. songs.Select(ToSong)], Slots);
    }

    /// <summary>Needs aliases <c>g</c> (grab_bag_songs) and <c>a</c> (artists).</summary>
    public const string SongColumns =
        """
        g.id AS Id, g.position::int AS Position, g.spotify_track_id AS SpotifyTrackId, g.title AS Title,
        a.id AS ArtistId, a.name AS ArtistName, a.image_url AS ArtistImageUrl, a.status::text AS ArtistStatus,
        g.album_title AS AlbumTitle, g.cover_url AS CoverUrl, g.added_at AS AddedAt
        """;

    public static GrabBagSong ToSong(SongRow row) =>
        new(row.Id, row.Position, row.SpotifyTrackId, row.Title, new ScoringEndpoints.ArtistRef(row.ArtistId, row.ArtistName, row.ArtistImageUrl),
            row.ArtistStatus, row.AlbumTitle, row.CoverUrl, SpotifyUrl(row.SpotifyTrackId), row.AddedAt);

    public sealed record SongRow(
        Guid Id, int Position, string SpotifyTrackId, string Title, Guid ArtistId, string ArtistName, string? ArtistImageUrl,
        string ArtistStatus, string? AlbumTitle, string? CoverUrl, DateTime AddedAt);

    [GeneratedRegex("^[0-9A-Za-z]{22}$")]
    private static partial Regex SpotifyIdPattern();
}
