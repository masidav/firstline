using System.Security.Claims;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.SpotifySync;
using FandomScore.Api.Infrastructure;
using FandomScore.Spotify;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Api.Features.SpotifyPlayer;

/// <summary>
/// openapi <c>/me/spotify/library</c>: the app's Like buttons (requested 2026-10-31). Whether songs and albums are
/// in the fan's Spotify library, and saving or removing them there. Saving is the one thing the app writes to a
/// fan's Spotify library, and only when they press the button. Nothing is stored here; the next sync reads the
/// library like any other change the fan makes in Spotify. See README.md here.
/// </summary>
public static class SpotifyLibraryEndpoints
{
    public static void MapSpotifyLibrary(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/spotify/library", GetAsync);
        v1.MapPut("/me/spotify/library/tracks/{spotifyTrackId}", (ClaimsPrincipal user, string spotifyTrackId, SpotifyPlayerAccess access,
            SpotifyClient spotify, NpgsqlDataSource db, IOptions<SpotifyOptions> options, CancellationToken ct) =>
            ChangeAsync(user, spotifyTrackId, "spotify_track_id", access, db, options, (token, c) => spotify.SaveTrackAsync(token, spotifyTrackId, c), syncAfter: true, ct));
        v1.MapDelete("/me/spotify/library/tracks/{spotifyTrackId}", (ClaimsPrincipal user, string spotifyTrackId, SpotifyPlayerAccess access,
            SpotifyClient spotify, NpgsqlDataSource db, IOptions<SpotifyOptions> options, CancellationToken ct) =>
            ChangeAsync(user, spotifyTrackId, "spotify_track_id", access, db, options, (token, c) => spotify.RemoveTrackAsync(token, spotifyTrackId, c), syncAfter: false, ct));
        v1.MapPut("/me/spotify/library/albums/{spotifyAlbumId}", (ClaimsPrincipal user, string spotifyAlbumId, SpotifyPlayerAccess access,
            SpotifyClient spotify, NpgsqlDataSource db, IOptions<SpotifyOptions> options, CancellationToken ct) =>
            ChangeAsync(user, spotifyAlbumId, "spotify_album_id", access, db, options, (token, c) => spotify.SaveAlbumAsync(token, spotifyAlbumId, c), syncAfter: true, ct));
        v1.MapDelete("/me/spotify/library/albums/{spotifyAlbumId}", (ClaimsPrincipal user, string spotifyAlbumId, SpotifyPlayerAccess access,
            SpotifyClient spotify, NpgsqlDataSource db, IOptions<SpotifyOptions> options, CancellationToken ct) =>
            ChangeAsync(user, spotifyAlbumId, "spotify_album_id", access, db, options, (token, c) => spotify.RemoveAlbumAsync(token, spotifyAlbumId, c), syncAfter: false, ct));
    }

    public sealed record SavedTrack(string SpotifyTrackId, bool Saved);

    public sealed record SavedAlbum(string SpotifyAlbumId, bool Saved);

    /// <summary>Status: ok, reconnect_needed, not_connected. The lists are empty unless ok.</summary>
    public sealed record LibraryState(string Status, IReadOnlyList<SavedTrack> Tracks, IReadOnlyList<SavedAlbum> Albums)
    {
        public static readonly LibraryState NotConnected = new("not_connected", [], []);
        public static readonly LibraryState ReconnectNeeded = new("reconnect_needed", [], []);
    }

    // Never an error for the connection's state: the buttons just hide or ask the fan to reconnect.
    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user, [FromQuery(Name = "track_ids")] string? trackIds, [FromQuery(Name = "album_ids")] string? albumIds,
        SpotifyPlayerAccess access, SpotifyClient spotify,
        IOptions<SpotifyOptions> options, CancellationToken cancellationToken)
    {
        var tracks = ParseIds(trackIds, "track_ids", SpotifyClient.MaxLibraryTrackIds);
        var albums = ParseIds(albumIds, "album_ids", SpotifyClient.MaxLibraryAlbumIds);
        if (!options.Value.IsEnabled)
        {
            return Results.Ok(LibraryState.NotConnected);
        }

        var userId = user.UserId();
        var connection = await access.LoadConnectionAsync(userId, cancellationToken);
        if (connection is null)
        {
            return Results.Ok(LibraryState.NotConnected);
        }

        if (!connection.CanUseLibrary)
        {
            return Results.Ok(LibraryState.ReconnectNeeded);
        }

        try
        {
            var (savedTracks, savedAlbums) = await Spotify(() => access.CallAsync(userId, connection, async token =>
            {
                var t = await spotify.GetSavedTracksAsync(token, tracks, cancellationToken);
                var a = await spotify.GetSavedAlbumsAsync(token, albums, cancellationToken);
                return (t, a);
            }, cancellationToken));
            return Results.Ok(new LibraryState("ok",
                [.. tracks.Select((id, i) => new SavedTrack(id, savedTracks[i]))],
                [.. albums.Select((id, i) => new SavedAlbum(id, savedAlbums[i]))]));
        }
        catch (ApiProblemException e) when (e.Code == "spotify_reauth_required")
        {
            return Results.Ok(LibraryState.ReconnectNeeded);
        }
        catch (ApiProblemException e) when (e.Code == "spotify_not_connected")
        {
            return Results.Ok(LibraryState.NotConnected);
        }
    }

    private static async Task<IResult> ChangeAsync(
        ClaimsPrincipal user, string id, string field, SpotifyPlayerAccess access, NpgsqlDataSource db, IOptions<SpotifyOptions> options,
        Func<string, CancellationToken, Task> change, bool syncAfter, CancellationToken cancellationToken)
    {
        if (!SpotifyPlayerEndpoints.SpotifyIdPattern().IsMatch(id))
        {
            throw ApiProblemException.Validation(new FieldError(field, "Send a Spotify id (22 letters and digits)."));
        }

        if (!options.Value.IsEnabled)
        {
            throw SpotifyConnections.Unavailable("Spotify is not set up on this server.");
        }

        var userId = user.UserId();
        var connection = await access.LoadConnectionAsync(userId, cancellationToken) ?? throw SpotifyConnections.NotConnected();
        if (!connection.CanUseLibrary)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "spotify_reauth_required", "Spotify needs to be connected again",
                "Connect Spotify again to let the app save songs and albums to your library.");
        }

        await Spotify(() => access.CallAsync(userId, connection, async token =>
        {
            await change(token, cancellationToken);
            return true;
        }, cancellationToken));

        if (syncAfter)
        {
            // The like counts for the fan's score once synced; a quiet sync brings that forward (15-minute gate).
            await using var dbConnection = await db.OpenConnectionAsync(cancellationToken);
            await using var transaction = await dbConnection.BeginTransactionAsync(cancellationToken);
            await SpotifyConnections.RequestBackgroundSyncAsync(dbConnection, transaction, userId, options.Value.ManualSyncInterval);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    private static List<string> ParseIds(string? list, string field, int max)
    {
        var ids = (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > max)
        {
            throw ApiProblemException.Validation(new FieldError(field, $"Send at most {max} ids."));
        }

        if (ids.Any(id => !SpotifyPlayerEndpoints.SpotifyIdPattern().IsMatch(id)))
        {
            throw ApiProblemException.Validation(new FieldError(field, "Send Spotify ids (22 letters and digits), separated by commas."));
        }

        return ids;
    }

    // Spotify's refusals as this API's problem codes. A 403 on a library call means the scope is gone (the fan
    // changed the app's permissions in Spotify): connecting again fixes it.
    private static async Task<T> Spotify<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (SpotifyRequestException e) when (e.IsForbidden)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "spotify_reauth_required", "Spotify needs to be connected again",
                "Spotify no longer lets the app change your library. Connect Spotify again.");
        }
        catch (SpotifyRequestException)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "spotify_library_refused", "Spotify didn't do that",
                "Spotify didn't accept that right now. Try it in the Spotify app.");
        }
        catch (SpotifyUnavailableException e)
        {
            throw SpotifyConnections.Unavailable("Spotify did not respond. Try again shortly.", e.RetryAfter);
        }
    }
}
