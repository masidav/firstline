namespace FandomScore.Spotify;

/// <summary>From the token endpoint. <see cref="RefreshToken"/> is null when a refresh keeps the old one.</summary>
public sealed record SpotifyTokens(string AccessToken, string? RefreshToken, IReadOnlyList<string> Scopes)
{
    /// <summary>How long the access token is good for (Spotify says one hour).</summary>
    public TimeSpan ExpiresIn { get; init; } = TimeSpan.FromHours(1);
}

public sealed record SpotifyUser(string Id);

/// <summary>A track with the ids needed for scoring. <see cref="ArtistIds"/> are in credit order.</summary>
public sealed record SpotifyTrack(string Id, string? AlbumId, IReadOnlyList<string> ArtistIds);

public sealed record SpotifySavedTrack(SpotifyTrack Track, DateTimeOffset? SavedAt);

/// <summary>A saved album: its id, its artists in credit order (the first is primary), and when it was saved.</summary>
public sealed record SpotifySavedAlbum(string AlbumId, IReadOnlyList<string> ArtistIds, DateTimeOffset? SavedAt);

/// <summary>One play from the fan's history.</summary>
public sealed record SpotifyPlay(SpotifyTrack Track, DateTimeOffset PlayedAt);

/// <summary>
/// A track in one of the fan's playlists. <see cref="AddedAt"/> is Spotify's <c>added_at</c> for the item; null when
/// Spotify omits it (it does for some very old playlists) or dates it to the epoch.
/// </summary>
public sealed record SpotifyPlaylistItem(SpotifyTrack Track, DateTimeOffset? AddedAt);

/// <summary>A playlist the fan owns or collaborates on, with its items.</summary>
public sealed record SpotifyPlaylist(string Id, bool Collaborative, IReadOnlyList<SpotifyPlaylistItem> Items)
{
    /// <summary>The playlist's tracks, in order.</summary>
    public IReadOnlyList<SpotifyTrack> Tracks => [.. Items.Select(i => i.Track)];
}

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

    /// <summary>Every item in the fan's own or collaborative playlists, with when it was added.</summary>
    public IReadOnlyList<SpotifyPlaylistItem> PlaylistItems => [.. Playlists.SelectMany(p => p.Items)];
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

public sealed record SpotifyTrackArtist(string Id, string Name);

/// <summary>
/// A track from search or lookup (grab bags). <see cref="Artists"/> are in credit order; the first is the primary
/// artist. <see cref="CoverUrl"/> is the album's largest cover image; <see cref="SpotifyUrl"/> opens the track on Spotify.
/// </summary>
public sealed record SpotifyTrackDetail(
    string Id, string Name, IReadOnlyList<SpotifyTrackArtist> Artists, string? AlbumId, string? AlbumName, string? CoverUrl,
    string SpotifyUrl);

/// <summary>A device the fan's Spotify can play on. <see cref="Id"/> is null for devices Spotify won't let apps target.</summary>
public sealed record SpotifyDevice(string? Id, string Name, string Type, bool IsActive, bool IsRestricted);

/// <summary>
/// What the fan's Spotify is doing right now. <see cref="Track"/> is null for podcasts, ads, and local files.
/// Held in memory only and shown to the fan; never stored.
/// </summary>
public sealed record SpotifyPlayback(bool IsPlaying, int? ProgressMs, int? DurationMs, SpotifyTrackDetail? Track, SpotifyDevice? Device);
