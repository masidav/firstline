namespace FandomScore.Spotify;

/// <summary>From the token endpoint. <see cref="RefreshToken"/> is null when a refresh keeps the old one.</summary>
public sealed record SpotifyTokens(string AccessToken, string? RefreshToken, IReadOnlyList<string> Scopes);

public sealed record SpotifyUser(string Id);

/// <summary>A track with the ids needed for scoring. <see cref="ArtistIds"/> are in credit order.</summary>
public sealed record SpotifyTrack(string Id, string? AlbumId, IReadOnlyList<string> ArtistIds);

public sealed record SpotifySavedTrack(SpotifyTrack Track, DateTimeOffset? SavedAt);

/// <summary>A saved album: its id, its artists in credit order (the first is primary), and when it was saved.</summary>
public sealed record SpotifySavedAlbum(string AlbumId, IReadOnlyList<string> ArtistIds, DateTimeOffset? SavedAt);

/// <summary>One play from the fan's history.</summary>
public sealed record SpotifyPlay(SpotifyTrack Track, DateTimeOffset PlayedAt);

/// <summary>A playlist the fan owns or collaborates on, with its tracks.</summary>
public sealed record SpotifyPlaylist(string Id, bool Collaborative, IReadOnlyList<SpotifyTrack> Tracks);

/// <summary>
/// One user's listening signals as Spotify returned them, for every artist. Held in memory only: the sync
/// worker keeps tracked artists' data and drops the rest before anything is stored.
/// </summary>
/// <param name="TopArtists">Top artists of the last 6 months, in rank order (index + 1 is the rank).</param>
/// <param name="TopArtistsShort">Top artists of the last 4 weeks.</param>
/// <param name="TopArtistsLong">Top artists over the fan's history.</param>
/// <param name="TopTracks">Distinct top tracks across the three time ranges.</param>
/// <param name="FollowedArtistIds">Artists the fan follows; null when the connection lacks scope user-follow-read.</param>
public sealed record SpotifyLibrary(
    IReadOnlyList<SpotifySavedTrack> SavedTracks,
    IReadOnlyList<SpotifyPlaylist> Playlists,
    IReadOnlyList<SpotifyArtist> TopArtists,
    IReadOnlyList<SpotifyArtist> TopArtistsShort,
    IReadOnlyList<SpotifyArtist> TopArtistsLong,
    IReadOnlyList<SpotifyTrack> TopTracks,
    IReadOnlyList<SpotifySavedAlbum> SavedAlbums,
    IReadOnlyList<string>? FollowedArtistIds,
    IReadOnlyList<SpotifyPlay> RecentlyPlayed)
{
    /// <summary>Top artists' (6 months) Spotify ids in rank order.</summary>
    public IReadOnlyList<string> TopArtistIds => [.. TopArtists.Select(a => a.Id)];

    /// <summary>Every track in the fan's own or collaborative playlists (a track in two playlists appears twice).</summary>
    public IReadOnlyList<SpotifyTrack> PlaylistTracks => [.. Playlists.SelectMany(p => p.Tracks)];
}

/// <summary>An artist from search or lookup. <see cref="ImageUrl"/> is the smallest image at least 300 px wide.</summary>
public sealed record SpotifyArtist(string Id, string Name, string? ImageUrl, IReadOnlyList<string> Genres);

/// <summary>
/// A release from an artist's catalog (ADR 0006). <see cref="ArtistIds"/> are in credit order; the first is the
/// primary artist. <see cref="ReleaseDate"/> is Spotify's string: a year, a month, or a day, per
/// <see cref="ReleaseDatePrecision"/>. <see cref="CoverUrl"/> is the largest cover image.
/// </summary>
public sealed record SpotifyAlbum(
    string Id, string Name, string AlbumType, string? ReleaseDate, string? ReleaseDatePrecision, string? CoverUrl,
    IReadOnlyList<string> ArtistIds);

/// <summary>A track as listed on its album.</summary>
public sealed record SpotifyAlbumTrack(string Id, string Name, int DiscNumber, int TrackNumber);
