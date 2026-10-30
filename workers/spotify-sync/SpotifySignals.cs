using FandomScore.Spotify;

namespace FandomScore.Worker.SpotifySync;

/// <summary>One row of <c>user_artist_signals</c>.</summary>
public sealed record ArtistSignals(
    Guid ArtistId, int LikedTracks, int PlaylistTracks, int DistinctAlbums, short? TopArtistRank, int RecentPlays,
    short? TopArtistRankShort, short? TopArtistRankLong, int TopTracks, int PlaylistsFeaturing, int DedicatedPlaylists,
    bool Followed);

/// <summary>
/// A track for <c>user_track_likes</c>; stored only if the track is in our catalog. <see cref="SavedAt"/> is Spotify's
/// <c>added_at</c>: of the like, or the earliest playlist add of the track. Null when Spotify gave none; a playlist row
/// is then dated when the sync first saw it (migration 0048).
/// </summary>
public sealed record TrackSignal(string SpotifyTrackId, string Source, DateTime? SavedAt);

/// <summary>A saved album for <c>user_album_saves</c>; stored only if the album is in our catalog.</summary>
public sealed record AlbumSave(string SpotifyAlbumId, DateTime? SavedAt);

/// <summary>A play of a tracked artist's track for the <c>user_plays</c> ledger.</summary>
public sealed record PlaySignal(Guid ArtistId, string SpotifyTrackId, DateTime PlayedAt);

public sealed record SyncedSignals(
    IReadOnlyList<ArtistSignals> Artists, IReadOnlyList<TrackSignal> Tracks, IReadOnlyList<AlbumSave> Albums,
    IReadOnlyList<PlaySignal> Plays);

/// <summary>
/// Reduces a Spotify library to tracked artists. Everything about other artists is dropped here, before
/// anything is stored (CLAUDE.md: only tracked artists are synced).
/// </summary>
public static class SpotifySignals
{
    /// <summary>A playlist counts as dedicated to an artist when at least this share of its tracks are theirs.</summary>
    public const double DedicatedShare = 0.8;

    /// <summary>...and it has at least this many tracks.</summary>
    public const int DedicatedMinTracks = 5;

    /// <param name="trackedArtists">Spotify artist id → artist id, for active tracked artists only.</param>
    public static SyncedSignals Reduce(SpotifyLibrary library, IReadOnlyDictionary<string, Guid> trackedArtists)
    {
        // A track counts for its primary (first-credited) artist only, as albums.artist_id does. How co-credited
        // tracks should count is an open product decision in PROJECT_TODO.md; change it here once decided.
        Guid? ArtistOf(SpotifyTrack track) => PrimaryOf(track.ArtistIds);
        Guid? PrimaryOf(IReadOnlyList<string> artistIds) =>
            artistIds is [var primary, ..] && trackedArtists.TryGetValue(primary, out var id) ? id : null;

        var liked = library.SavedTracks
            .Where(s => ArtistOf(s.Track) is not null)
            .DistinctBy(s => s.Track.Id)
            .ToList();
        // Distinct tracks, so copying a track into many playlists counts once, dated by its first add.
        var playlistAdds = library.PlaylistItems
            .Where(i => ArtistOf(i.Track) is not null)
            .GroupBy(i => i.Track.Id)
            .Select(g => new SpotifyPlaylistItem(g.First().Track, g.Min(i => i.AddedAt)))
            .ToList();
        var inPlaylists = playlistAdds.Select(i => i.Track).ToList();
        var recent = library.RecentlyPlayed.Where(p => ArtistOf(p.Track) is not null).ToList();
        var topRanks = Ranks(library.TopArtists);
        var topRanksShort = Ranks(library.TopArtistsShort);
        var topRanksLong = Ranks(library.TopArtistsLong);
        var topTracks = library.TopTracks.Where(t => ArtistOf(t) is not null).DistinctBy(t => t.Id).ToList();
        var savedAlbums = library.SavedAlbums.Where(a => PrimaryOf(a.ArtistIds) is not null).DistinctBy(a => a.AlbumId).ToList();
        var followed = (library.FollowedArtistIds ?? [])
            .Select(id => trackedArtists.TryGetValue(id, out var artistId) ? artistId : (Guid?)null)
            .OfType<Guid>()
            .ToHashSet();
        // Per playlist: how many tracks each tracked artist has, and the playlist's size.
        var playlistCounts = library.Playlists
            .Select(p => (Total: p.Tracks.Count, ByArtist: p.Tracks
                .Select(ArtistOf).OfType<Guid>().GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count())))
            .ToList();

        var artistIds = liked.Select(s => ArtistOf(s.Track)!.Value)
            .Concat(inPlaylists.Select(t => ArtistOf(t)!.Value))
            .Concat(recent.Select(p => ArtistOf(p.Track)!.Value))
            .Concat(topRanks.Keys).Concat(topRanksShort.Keys).Concat(topRanksLong.Keys)
            .Concat(topTracks.Select(t => ArtistOf(t)!.Value))
            .Concat(savedAlbums.Select(a => PrimaryOf(a.ArtistIds)!.Value))
            .Concat(followed)
            .Distinct();

        var artists = artistIds.Select(artistId =>
        {
            var likedTracks = liked.Where(s => ArtistOf(s.Track) == artistId).Select(s => s.Track).ToList();
            var playlistTracks = inPlaylists.Where(t => ArtistOf(t) == artistId).ToList();
            var featuring = playlistCounts.Where(p => p.ByArtist.ContainsKey(artistId)).ToList();
            return new ArtistSignals(
                artistId,
                likedTracks.Count,
                playlistTracks.Count,
                likedTracks.Concat(playlistTracks).Select(t => t.AlbumId).OfType<string>().Distinct().Count(),
                topRanks.TryGetValue(artistId, out var rank) ? rank : null,
                recent.Count(p => ArtistOf(p.Track) == artistId),
                topRanksShort.TryGetValue(artistId, out var rankShort) ? rankShort : null,
                topRanksLong.TryGetValue(artistId, out var rankLong) ? rankLong : null,
                topTracks.Count(t => ArtistOf(t) == artistId),
                featuring.Count,
                featuring.Count(p => p.Total >= DedicatedMinTracks && p.ByArtist[artistId] >= DedicatedShare * p.Total),
                followed.Contains(artistId));
        }).ToList();

        var tracks = liked.Select(s => new TrackSignal(s.Track.Id, "liked", s.SavedAt?.UtcDateTime))
            .Concat(playlistAdds.Select(i => new TrackSignal(i.Track.Id, "playlist", i.AddedAt?.UtcDateTime)))
            .ToList();
        var albums = savedAlbums.Select(a => new AlbumSave(a.AlbumId, a.SavedAt?.UtcDateTime)).ToList();
        var plays = recent.Select(p => new PlaySignal(ArtistOf(p.Track)!.Value, p.Track.Id, p.PlayedAt.UtcDateTime)).ToList();
        return new SyncedSignals(artists, tracks, albums, plays);

        Dictionary<Guid, short> Ranks(IReadOnlyList<SpotifyArtist> top)
        {
            var ranks = new Dictionary<Guid, short>();
            for (var i = 0; i < top.Count; i++)
            {
                if (trackedArtists.TryGetValue(top[i].Id, out var id))
                {
                    ranks.TryAdd(id, (short)(i + 1));
                }
            }

            return ranks;
        }
    }

    /// <summary>
    /// Keeps the plays of tracked, active artists from a poll of the fan's recent plays (the rest is dropped here).
    /// </summary>
    public static IReadOnlyList<PlaySignal> ReducePlays(IReadOnlyList<SpotifyPlay> plays, IReadOnlyDictionary<string, Guid> trackedArtists) =>
        [.. plays
            .Select(p => p.Track.ArtistIds is [var primary, ..] && trackedArtists.TryGetValue(primary, out var id)
                ? new PlaySignal(id, p.Track.Id, p.PlayedAt.UtcDateTime)
                : null)
            .OfType<PlaySignal>()];
}
