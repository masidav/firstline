using FandomScore.Spotify;

namespace FandomScore.Worker.SpotifySync;

/// <summary>One row of <c>user_artist_signals</c>.</summary>
public sealed record ArtistSignals(
    Guid ArtistId, int LikedTracks, int PlaylistTracks, int DistinctAlbums, short? TopArtistRank, int RecentPlays);

/// <summary>A track for <c>user_track_likes</c>; stored only if the track is in our catalog.</summary>
public sealed record TrackSignal(string SpotifyTrackId, string Source, DateTime? SavedAt);

public sealed record SyncedSignals(IReadOnlyList<ArtistSignals> Artists, IReadOnlyList<TrackSignal> Tracks);

/// <summary>
/// Reduces a Spotify library to tracked artists. Everything about other artists is dropped here, before
/// anything is stored (CLAUDE.md: only tracked artists are synced).
/// </summary>
public static class SpotifySignals
{
    /// <param name="trackedArtists">Spotify artist id → artist id, for active tracked artists only.</param>
    public static SyncedSignals Reduce(SpotifyLibrary library, IReadOnlyDictionary<string, Guid> trackedArtists)
    {
        // A track counts for its primary (first-credited) artist only, as albums.artist_id does. How co-credited
        // tracks should count is an open product decision in PROJECT_TODO.md; change it here once decided.
        Guid? ArtistOf(SpotifyTrack track) =>
            track.ArtistIds is [var primary, ..] && trackedArtists.TryGetValue(primary, out var id) ? id : null;

        var liked = library.SavedTracks
            .Where(s => ArtistOf(s.Track) is not null)
            .DistinctBy(s => s.Track.Id)
            .ToList();
        // Distinct tracks, so copying a track into many playlists counts once.
        var inPlaylists = library.PlaylistTracks
            .Where(t => ArtistOf(t) is not null)
            .DistinctBy(t => t.Id)
            .ToList();
        var recent = library.RecentlyPlayed.Where(t => ArtistOf(t) is not null).ToList();
        var topRanks = new Dictionary<Guid, short>();
        for (var i = 0; i < library.TopArtistIds.Count; i++)
        {
            if (trackedArtists.TryGetValue(library.TopArtistIds[i], out var id))
            {
                topRanks.TryAdd(id, (short)(i + 1));
            }
        }

        var artistIds = liked.Select(s => ArtistOf(s.Track)!.Value)
            .Concat(inPlaylists.Select(t => ArtistOf(t)!.Value))
            .Concat(recent.Select(t => ArtistOf(t)!.Value))
            .Concat(topRanks.Keys)
            .Distinct();

        var artists = artistIds.Select(artistId =>
        {
            var likedTracks = liked.Where(s => ArtistOf(s.Track) == artistId).Select(s => s.Track).ToList();
            var playlistTracks = inPlaylists.Where(t => ArtistOf(t) == artistId).ToList();
            return new ArtistSignals(
                artistId,
                likedTracks.Count,
                playlistTracks.Count,
                likedTracks.Concat(playlistTracks).Select(t => t.AlbumId).OfType<string>().Distinct().Count(),
                topRanks.TryGetValue(artistId, out var rank) ? rank : null,
                recent.Count(t => ArtistOf(t) == artistId));
        }).ToList();

        var tracks = liked.Select(s => new TrackSignal(s.Track.Id, "liked", s.SavedAt?.UtcDateTime))
            .Concat(inPlaylists.Select(t => new TrackSignal(t.Id, "playlist", null)))
            .ToList();
        return new SyncedSignals(artists, tracks);
    }
}
