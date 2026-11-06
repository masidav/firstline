using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FandomScore.MusicProviders;

namespace FandomScore.Worker.MusicSync;

/// <summary>A tracked, active artist's catalog as the matcher needs it.</summary>
public sealed record CatalogArtist(Guid Id, string Name, IReadOnlyList<CatalogTrack> Tracks, IReadOnlyList<CatalogAlbum> Albums);

public sealed record CatalogTrack(Guid Id, string Title, string? Isrc, Guid AlbumId, string AlbumTitle, string? SpotifyTrackId);

public sealed record CatalogAlbum(Guid Id, string Title, string? Upc);

/// <summary>One row of <c>user_artist_signals</c> for a provider.</summary>
public sealed record MusicArtistSignals(
    Guid ArtistId, int LikedTracks, int PlaylistTracks, int DistinctAlbums, int RecentPlays, short? TopArtistRankShort,
    int PlaylistsFeaturing, int DedicatedPlaylists, bool Followed);

/// <summary>A matched track for <c>user_track_likes</c>: liked or in a playlist, dated when the provider dates it.</summary>
public sealed record MusicTrackSignal(Guid TrackId, string Source, DateTime? SavedAt);

public sealed record MusicAlbumSave(Guid AlbumId, DateTime? SavedAt);

/// <summary>A code the provider gave for a catalog item we matched by title: stored so later matches go by code.</summary>
public sealed record LearnedCode(Guid Id, string Code);

public sealed record MatchedSignals(
    IReadOnlyList<MusicArtistSignals> Artists, IReadOnlyList<MusicTrackSignal> Tracks, IReadOnlyList<MusicAlbumSave> Albums,
    IReadOnlyList<LearnedCode> TrackIsrcs, IReadOnlyList<LearnedCode> AlbumUpcs);

/// <summary>
/// Reduces a provider's library to tracked artists (ADR 0007). Apple Music and Tidal ids mean nothing to our Spotify
/// keyed catalog, so matching goes: artist by normalized name (a name two tracked artists share is skipped as
/// ambiguous), then track by ISRC when both sides have one, else by normalized title (the album title breaks a tie
/// between editions); albums by UPC, else by normalized title. Everything about other artists is dropped here,
/// before anything is stored (CLAUDE.md).
/// </summary>
public static partial class MusicMatcher
{
    public const double DedicatedShare = 0.8;
    public const int DedicatedMinTracks = 5;

    public static MatchedSignals Reduce(MusicLibrary library, IReadOnlyList<CatalogArtist> catalog)
    {
        var artistsByName = catalog.GroupBy(a => Normalize(a.Name), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0 && g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        CatalogArtist? ArtistOf(string name) => artistsByName.GetValueOrDefault(Normalize(name));

        var learnedIsrcs = new Dictionary<Guid, string>();
        var learnedUpcs = new Dictionary<Guid, string>();
        CatalogTrack? MatchTrack(ProviderTrack track)
        {
            if (ArtistOf(track.ArtistName) is not { } artist)
            {
                return null;
            }

            if (track.Isrc is { } isrc && artist.Tracks.FirstOrDefault(t => t.Isrc == isrc) is { } byCode)
            {
                return byCode;
            }

            var title = Normalize(track.Title);
            var candidates = artist.Tracks.Where(t => Normalize(t.Title) == title).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var albumTitle = track.AlbumTitle is null ? null : Normalize(track.AlbumTitle);
            var match = candidates.FirstOrDefault(t => albumTitle is not null && Normalize(t.AlbumTitle) == albumTitle) ?? candidates[0];
            if (track.Isrc is { } code && match.Isrc is null)
            {
                learnedIsrcs.TryAdd(match.Id, code);
            }

            return match;
        }

        CatalogAlbum? MatchAlbum(ProviderAlbum album)
        {
            if (ArtistOf(album.ArtistName) is not { } artist)
            {
                return null;
            }

            if (album.Upc is { } upc && artist.Albums.FirstOrDefault(a => a.Upc == upc) is { } byCode)
            {
                return byCode;
            }

            var title = Normalize(album.Title);
            var match = artist.Albums.FirstOrDefault(a => Normalize(a.Title) == title);
            if (match is not null && album.Upc is { } code && match.Upc is null)
            {
                learnedUpcs.TryAdd(match.Id, code);
            }

            return match;
        }

        var artistOfTrack = catalog.SelectMany(a => a.Tracks.Select(t => (t.Id, a.Id))).ToDictionary(x => x.Item1, x => x.Item2);
        var artistOfAlbum = catalog.SelectMany(a => a.Albums.Select(al => (al.Id, a.Id))).ToDictionary(x => x.Item1, x => x.Item2);
        var albumOfTrack = catalog.SelectMany(a => a.Tracks).ToDictionary(t => t.Id, t => t.AlbumId);

        var liked = library.SavedTracks
            .Select(t => (Track: MatchTrack(t), t.AddedAt))
            .Where(x => x.Track is not null)
            .GroupBy(x => x.Track!.Id)
            .Select(g => (TrackId: g.Key, AddedAt: g.Min(x => x.AddedAt)))
            .ToList();
        // Distinct tracks across playlists, dated by the earliest add.
        var playlistTracks = library.Playlists
            .SelectMany(p => p.Tracks.Select(t => (Track: MatchTrack(t), t.AddedAt)))
            .Where(x => x.Track is not null)
            .GroupBy(x => x.Track!.Id)
            .Select(g => (TrackId: g.Key, AddedAt: g.Min(x => x.AddedAt)))
            .ToList();
        var playlistCounts = library.Playlists
            .Select(p => (Total: p.Tracks.Count, ByArtist: p.Tracks.Select(MatchTrack).OfType<CatalogTrack>()
                .GroupBy(t => artistOfTrack[t.Id]).ToDictionary(g => g.Key, g => g.Count())))
            .ToList();
        var recent = library.RecentlyPlayed.Select(MatchTrack).OfType<CatalogTrack>().ToList();
        var albums = library.SavedAlbums
            .Select(a => (Album: MatchAlbum(a), a.AddedAt))
            .Where(x => x.Album is not null)
            .GroupBy(x => x.Album!.Id)
            .Select(g => (AlbumId: g.Key, AddedAt: g.Min(x => x.AddedAt)))
            .ToList();
        var heavy = new Dictionary<Guid, short>();
        for (var i = 0; i < library.HeavyRotationArtistNames.Count; i++)
        {
            if (ArtistOf(library.HeavyRotationArtistNames[i]) is { } artist)
            {
                heavy.TryAdd(artist.Id, (short)(i + 1));
            }
        }

        var followed = library.FollowedArtistNames.Select(ArtistOf).OfType<CatalogArtist>().Select(a => a.Id).ToHashSet();

        var artistIds = liked.Select(l => artistOfTrack[l.TrackId])
            .Concat(playlistTracks.Select(p => artistOfTrack[p.TrackId]))
            .Concat(recent.Select(t => artistOfTrack[t.Id]))
            .Concat(albums.Select(a => artistOfAlbum[a.AlbumId]))
            .Concat(heavy.Keys)
            .Concat(followed)
            .Distinct();

        var signals = artistIds.Select(artistId =>
        {
            var likedHere = liked.Where(l => artistOfTrack[l.TrackId] == artistId).ToList();
            var inPlaylists = playlistTracks.Where(p => artistOfTrack[p.TrackId] == artistId).ToList();
            var featuring = playlistCounts.Where(p => p.ByArtist.ContainsKey(artistId)).ToList();
            return new MusicArtistSignals(
                artistId,
                likedHere.Count,
                inPlaylists.Count,
                likedHere.Select(l => albumOfTrack[l.TrackId]).Concat(inPlaylists.Select(p => albumOfTrack[p.TrackId])).Distinct().Count(),
                recent.Count(t => artistOfTrack[t.Id] == artistId),
                heavy.TryGetValue(artistId, out var rank) ? rank : null,
                featuring.Count,
                featuring.Count(p => p.Total >= DedicatedMinTracks && p.ByArtist[artistId] >= DedicatedShare * p.Total),
                followed.Contains(artistId));
        }).ToList();

        return new MatchedSignals(
            signals,
            [.. liked.Select(l => new MusicTrackSignal(l.TrackId, "liked", l.AddedAt?.UtcDateTime))
                .Concat(playlistTracks.Select(p => new MusicTrackSignal(p.TrackId, "playlist", p.AddedAt?.UtcDateTime)))],
            [.. albums.Select(a => new MusicAlbumSave(a.AlbumId, a.AddedAt?.UtcDateTime))],
            [.. learnedIsrcs.Select(x => new LearnedCode(x.Key, x.Value))],
            [.. learnedUpcs.Select(x => new LearnedCode(x.Key, x.Value))]);
    }

    /// <summary>
    /// Lowercase, without accents, without a parenthesized or bracketed tail ("(feat. X)", "[Live]"), without a
    /// " - Remastered 2011" style suffix, and without anything but letters and digits. "The" at the start stays:
    /// "The National" and "National" are different artists.
    /// </summary>
    public static string Normalize(string value)
    {
        var text = Tail().Replace(value, "");
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (c == '&')
            {
                builder.Append("and");
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]|\s+-\s+.*$|\s+feat\..*$", RegexOptions.IgnoreCase)]
    private static partial Regex Tail();
}
