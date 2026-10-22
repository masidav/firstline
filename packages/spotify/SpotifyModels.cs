namespace FandomScore.Spotify;

/// <summary>From the token endpoint. <see cref="RefreshToken"/> is null when a refresh keeps the old one.</summary>
public sealed record SpotifyTokens(string AccessToken, string? RefreshToken, IReadOnlyList<string> Scopes);

public sealed record SpotifyUser(string Id);

/// <summary>A track with the ids needed for scoring. <see cref="ArtistIds"/> are in credit order.</summary>
public sealed record SpotifyTrack(string Id, string? AlbumId, IReadOnlyList<string> ArtistIds);

public sealed record SpotifySavedTrack(SpotifyTrack Track, DateTimeOffset? SavedAt);

/// <summary>
/// One user's listening signals as Spotify returned them, for every artist. Held in memory only: the sync
/// worker keeps tracked artists' data and drops the rest before anything is stored.
/// </summary>
public sealed record SpotifyLibrary(
    IReadOnlyList<SpotifySavedTrack> SavedTracks,
    IReadOnlyList<SpotifyTrack> PlaylistTracks,
    IReadOnlyList<string> TopArtistIds,
    IReadOnlyList<SpotifyTrack> RecentlyPlayed);

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
