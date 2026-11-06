namespace FandomScore.MusicProviders;

/// <summary>
/// A track as a provider describes it: what matching needs (ADR 0007). <see cref="Isrc"/> when the provider gives
/// one; <see cref="ArtistName"/> is the first credited artist; <see cref="AddedAt"/> is when the fan saved it.
/// </summary>
public sealed record ProviderTrack(string Id, string Title, string ArtistName, string? AlbumTitle, string? Isrc, DateTimeOffset? AddedAt);

/// <summary>A release in the fan's library. <see cref="Upc"/> (barcode) when the provider gives one.</summary>
public sealed record ProviderAlbum(string Id, string Title, string ArtistName, string? Upc, DateTimeOffset? AddedAt);

/// <summary>A playlist the fan owns, with its tracks (dated when the provider dates them).</summary>
public sealed record ProviderPlaylist(string Id, IReadOnlyList<ProviderTrack> Tracks);

/// <summary>
/// One fan's library as a provider returned it, every artist included. Held in memory only: the sync keeps tracked
/// artists' data and drops the rest before anything is stored (CLAUDE.md).
/// </summary>
/// <param name="HeavyRotationArtistNames">Apple Music's heavy rotation (most played lately), best first; empty elsewhere.</param>
/// <param name="FollowedArtistNames">Tidal's favourite artists; empty elsewhere.</param>
/// <param name="RecentlyPlayed">Recent plays in order, newest first, without times (neither provider dates them).</param>
public sealed record MusicLibrary(
    IReadOnlyList<ProviderTrack> SavedTracks,
    IReadOnlyList<ProviderAlbum> SavedAlbums,
    IReadOnlyList<ProviderPlaylist> Playlists,
    IReadOnlyList<ProviderTrack> RecentlyPlayed,
    IReadOnlyList<string> HeavyRotationArtistNames,
    IReadOnlyList<string> FollowedArtistNames);

/// <summary>From Tidal's token endpoint. <see cref="RefreshToken"/> is null when a refresh keeps the old one.</summary>
public sealed record TidalTokens(string AccessToken, string? RefreshToken, IReadOnlyList<string> Scopes, TimeSpan ExpiresIn);

public sealed record TidalUser(string Id, string? CountryCode);

/// <summary>What validating an Apple Music user token learns: the fan's storefront (country).</summary>
public sealed record AppleMusicUser(string Storefront);
