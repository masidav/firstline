using System.Globalization;
using Dapper;
using FandomScore.Spotify;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Worker.ArtistOnboarding;

/// <summary>What one load stored. Counts are the releases Spotify lists now, not rows ever stored.</summary>
public sealed record CatalogLoad(int Albums, int Tracks, int Delisted);

/// <summary>
/// Loads an artist's catalog from Spotify into <c>albums</c> and <c>tracks</c> (ADR 0006): the artist's own
/// releases in one market, credited to the primary artist only. Rows are upserted by Spotify id; releases
/// Spotify no longer lists are delisted, never deleted, so fans' logs survive. Running it again gives the
/// same result. Shared by onboarding and the weekly refresh.
/// </summary>
public sealed partial class CatalogLoader(SpotifyClient spotify, IOptions<SpotifyOptions> options, ILogger<CatalogLoader> logger)
{
    private readonly SpotifyOptions _options = options.Value;

    /// <summary>
    /// Fetches and stores the catalog in one transaction. Returns null when Spotify has no artist with this id.
    /// Throws <see cref="SpotifyUnavailableException"/> (or another Spotify error) with nothing changed.
    /// </summary>
    public async Task<CatalogLoad?> LoadAsync(
        NpgsqlConnection connection, Guid artistId, string spotifyArtistId, CancellationToken cancellationToken)
    {
        var listed = await spotify.GetArtistAlbumsAsync(spotifyArtistId, _options.CatalogMarket, _options.MaxCatalogAlbums, cancellationToken);
        if (listed is null)
        {
            return null;
        }

        // Primary artist only: a release credited first to someone else belongs to that artist's catalog.
        // Spotify can list a release twice (regional editions share an id across pages); the last one wins.
        var albums = new Dictionary<string, SpotifyAlbum>(StringComparer.Ordinal);
        foreach (var album in listed.Where(a => a.ArtistIds.Count > 0 && a.ArtistIds[0] == spotifyArtistId))
        {
            albums[album.Id] = album;
        }

        var tracks = new Dictionary<string, (string AlbumId, SpotifyAlbumTrack Track)>(StringComparer.Ordinal);
        foreach (var albumId in albums.Keys.ToList())
        {
            var albumTracks = await spotify.GetAlbumTracksAsync(albumId, _options.CatalogMarket, cancellationToken);
            if (albumTracks is null)
            {
                // Removed between the two calls; it will be delisted below.
                albums.Remove(albumId);
                continue;
            }

            foreach (var track in albumTracks)
            {
                tracks[track.Id] = (albumId, track);
            }
        }

        var delisted = await SaveAsync(connection, artistId, albums.Values, tracks.Values, cancellationToken);
        LogLoaded(logger, artistId, albums.Count, tracks.Count, delisted);
        return new CatalogLoad(albums.Count, tracks.Count, delisted);
    }

    private static async Task<int> SaveAsync(
        NpgsqlConnection connection, Guid artistId, IEnumerable<SpotifyAlbum> albums,
        IEnumerable<(string AlbumId, SpotifyAlbumTrack Track)> tracks, CancellationToken cancellationToken)
    {
        var albumRows = albums.Select(a =>
        {
            var (date, precision) = ParseReleaseDate(a.ReleaseDate, a.ReleaseDatePrecision);
            return (a.Id, a.Name, Type: AlbumType(a.AlbumType), Date: date, Precision: precision, a.CoverUrl);
        }).ToList();
        var trackRows = tracks.Select(t => (t.AlbumId, t.Track.Id, t.Track.Name, Disc: (short)Math.Max(1, t.Track.DiscNumber),
            Number: t.Track.TrackNumber > 0 ? (short?)t.Track.TrackNumber : null)).ToList();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var delisted = await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO albums (artist_id, spotify_album_id, title, album_type, release_date, release_date_precision,
                                cover_url, refreshed_at, delisted_at)
            SELECT @artistId, x.id, x.title, x.album_type, x.release_date, x.precision, x.cover_url, now(), NULL
            FROM unnest(@albumIds, @titles, @types, @dates, @precisions, @covers)
                 AS x (id, title, album_type, release_date, precision, cover_url)
            ON CONFLICT (spotify_album_id) DO UPDATE
            SET artist_id = EXCLUDED.artist_id, title = EXCLUDED.title, album_type = EXCLUDED.album_type,
                release_date = EXCLUDED.release_date, release_date_precision = EXCLUDED.release_date_precision,
                cover_url = EXCLUDED.cover_url, refreshed_at = now(), delisted_at = NULL;

            INSERT INTO tracks (album_id, spotify_track_id, title, disc_number, track_number, delisted_at)
            SELECT a.id, x.id, x.title, x.disc, x.number, NULL
            FROM unnest(@trackAlbumIds, @trackIds, @trackTitles, @discs, @numbers) AS x (album_id, id, title, disc, number)
            JOIN albums a ON a.spotify_album_id = x.album_id
            ON CONFLICT (spotify_track_id) DO UPDATE
            SET album_id = EXCLUDED.album_id, title = EXCLUDED.title, disc_number = EXCLUDED.disc_number,
                track_number = EXCLUDED.track_number, delisted_at = NULL;

            -- Releases and tracks Spotify no longer lists for this artist. Rows without a Spotify id were never
            -- loaded from Spotify and are left alone.
            WITH gone_albums AS (
              UPDATE albums SET delisted_at = now()
              WHERE artist_id = @artistId AND delisted_at IS NULL
                AND spotify_album_id IS NOT NULL AND spotify_album_id <> ALL(@albumIds)
              RETURNING id
            ), gone_tracks AS (
              UPDATE tracks t SET delisted_at = now()
              FROM albums a
              WHERE t.album_id = a.id AND a.artist_id = @artistId AND t.delisted_at IS NULL
                AND t.spotify_track_id IS NOT NULL AND t.spotify_track_id <> ALL(@trackIds)
              RETURNING t.id
            )
            SELECT (SELECT count(*) FROM gone_albums)::integer;

            UPDATE artists SET catalog_loaded_at = now() WHERE id = @artistId;
            """,
            new
            {
                artistId,
                albumIds = albumRows.Select(a => a.Id).ToArray(),
                titles = albumRows.Select(a => a.Name).ToArray(),
                types = albumRows.Select(a => a.Type).ToArray(),
                dates = albumRows.Select(a => a.Date).ToArray(),
                precisions = albumRows.Select(a => a.Precision).ToArray(),
                covers = albumRows.Select(a => a.CoverUrl).ToArray(),
                trackAlbumIds = trackRows.Select(t => t.AlbumId).ToArray(),
                trackIds = trackRows.Select(t => t.Id).ToArray(),
                trackTitles = trackRows.Select(t => t.Name).ToArray(),
                discs = trackRows.Select(t => t.Disc).ToArray(),
                numbers = trackRows.Select(t => t.Number).ToArray(),
            },
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return delisted;
    }

    private static string AlbumType(string type) => type is "single" or "compilation" ? type : "album";

    /// <summary>
    /// Spotify's release_date is "1981", "1981-12", or "1981-12-25" with a matching precision. The stored date is
    /// the first day of the period; the precision says how much of it is real. Unparseable dates are stored as null.
    /// </summary>
    public static (DateOnly? Date, string Precision) ParseReleaseDate(string? releaseDate, string? precision)
    {
        if (releaseDate is null)
        {
            return (null, "day");
        }

        var padded = releaseDate.Length switch
        {
            4 => releaseDate + "-01-01",
            7 => releaseDate + "-01",
            _ => releaseDate,
        };
        if (!DateOnly.TryParseExact(padded, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (null, "day");
        }

        var actual = precision is "year" or "month" ? precision : releaseDate.Length switch { 4 => "year", 7 => "month", _ => "day" };
        return (date, actual);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded catalog for artist {ArtistId}: {Albums} releases, {Tracks} tracks, {Delisted} delisted.")]
    private static partial void LogLoaded(ILogger logger, Guid artistId, int albums, int tracks, int delisted);
}
