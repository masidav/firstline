using System.Security.Claims;
using System.Text.RegularExpressions;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.SpotifySync;
using FandomScore.Api.Infrastructure;
using FandomScore.Spotify;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FandomScore.Api.Features.SpotifyPlayer;

/// <summary>
/// openapi <c>/me/spotify/player</c>: the fan's own Spotify playback for the app's now-playing bar, and play, pause,
/// and skip for its buttons (requested 2026-10-26). Spotify plays on the fan's phone or computer app (Spotify
/// Connect); nothing about playback is stored. See README.md here and docs/adr/0005-spotify-integration.md.
/// </summary>
public static partial class SpotifyPlayerEndpoints
{
    /// <summary>Several tabs or a quick re-render share one Spotify call per fan within this window.</summary>
    private static readonly TimeSpan StateCacheFor = TimeSpan.FromSeconds(3);

    // Device types a play may be sent to when nothing is active: the fan's own phone or computer, never a speaker
    // or TV elsewhere in the house.
    private static readonly string[] PersonalDevices = ["Smartphone", "Computer", "Tablet"];

    public static void MapSpotifyPlayer(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/spotify/player", GetAsync);
        v1.MapPut("/me/spotify/player/play", PlayAsync);
        v1.MapPut("/me/spotify/player/pause", PauseAsync);
        v1.MapPost("/me/spotify/player/next", (ClaimsPrincipal user, SpotifyPlayerAccess access, SpotifyClient spotify,
            IMemoryCache cache, IOptions<SpotifyOptions> options, CancellationToken ct) =>
            CommandAsync(user, access, cache, options, (token, c) => spotify.SkipAsync(token, forward: true, c), ct));
        v1.MapPost("/me/spotify/player/previous", (ClaimsPrincipal user, SpotifyPlayerAccess access, SpotifyClient spotify,
            IMemoryCache cache, IOptions<SpotifyOptions> options, CancellationToken ct) =>
            CommandAsync(user, access, cache, options, (token, c) => spotify.SkipAsync(token, forward: false, c), ct));
    }

    public sealed record PlayRequest(string? SpotifyTrackId);

    public sealed record PlayerArtist(string SpotifyArtistId, string Name);

    public sealed record PlayerTrack(
        string SpotifyTrackId, string Title, IReadOnlyList<PlayerArtist> Artists, string? AlbumTitle, string? CoverUrl,
        string SpotifyUrl, int? DurationMs);

    public sealed record PlayerDevice(string Name, string Type);

    /// <summary>Status: playing, paused, nothing_playing, reconnect_needed, not_connected.</summary>
    public sealed record PlayerState(string Status, PlayerTrack? Track, int? ProgressMs, PlayerDevice? Device)
    {
        public static readonly PlayerState NotConnected = new("not_connected", null, null, null);
        public static readonly PlayerState ReconnectNeeded = new("reconnect_needed", null, null, null);
        public static readonly PlayerState NothingPlaying = new("nothing_playing", null, null, null);
    }

    private static string StateKey(Guid userId) => $"spotify-player:{userId}";

    // Never an error for the connection's state: the bar just hides or asks the fan to reconnect.
    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user, SpotifyPlayerAccess access, SpotifyClient spotify, IMemoryCache cache,
        IOptions<SpotifyOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.IsEnabled)
        {
            return Results.Ok(PlayerState.NotConnected);
        }

        var userId = user.UserId();
        var connection = await access.LoadConnectionAsync(userId, cancellationToken);
        if (connection is null)
        {
            return Results.Ok(PlayerState.NotConnected);
        }

        if (!connection.CanUsePlayer)
        {
            return Results.Ok(PlayerState.ReconnectNeeded);
        }

        if (cache.TryGetValue(StateKey(userId), out PlayerState? recent) && recent is not null)
        {
            return Results.Ok(recent);
        }

        PlayerState state;
        try
        {
            var playback = await Spotify(() => access.CallAsync(userId, connection, token => spotify.GetPlaybackAsync(token, cancellationToken), cancellationToken));
            state = ToState(playback);
        }
        catch (ApiProblemException e) when (e.Code == "spotify_reauth_required")
        {
            state = PlayerState.ReconnectNeeded;
        }
        catch (ApiProblemException e) when (e.Code == "spotify_not_connected")
        {
            return Results.Ok(PlayerState.NotConnected);
        }

        cache.Set(StateKey(userId), state, StateCacheFor);
        return Results.Ok(state);
    }

    private static PlayerState ToState(SpotifyPlayback? playback)
    {
        if (playback?.Track is not { } track)
        {
            return PlayerState.NothingPlaying;
        }

        return new PlayerState(
            playback.IsPlaying ? "playing" : "paused",
            new PlayerTrack(track.Id, track.Name, [.. track.Artists.Select(a => new PlayerArtist(a.Id, a.Name))],
                track.AlbumName, track.CoverUrl, track.SpotifyUrl, playback.DurationMs),
            playback.ProgressMs,
            playback.Device is { } device ? new PlayerDevice(device.Name, device.Type) : null);
    }

    private static async Task<IResult> PlayAsync(
        ClaimsPrincipal user, PlayRequest? request, SpotifyPlayerAccess access, SpotifyClient spotify, IMemoryCache cache,
        IOptions<SpotifyOptions> options, CancellationToken cancellationToken)
    {
        var trackId = request?.SpotifyTrackId;
        if (trackId is not null && !TrackIdPattern().IsMatch(trackId))
        {
            throw ApiProblemException.Validation(new FieldError("spotify_track_id", "Send a Spotify track id (22 letters and digits)."));
        }

        return await CommandAsync(user, access, cache, options, async (token, ct) =>
        {
            try
            {
                await spotify.PlayAsync(token, trackId, deviceId: null, ct);
            }
            catch (SpotifyRequestException e) when (e.IsNoActiveDevice)
            {
                // Spotify is open but idle on the fan's phone or computer: start it there.
                var devices = await spotify.GetDevicesAsync(token, ct);
                var device = devices
                    .Where(d => d.Id is not null && !d.IsRestricted && PersonalDevices.Contains(d.Type, StringComparer.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.IsActive)
                    .ThenBy(d => Array.FindIndex(PersonalDevices, t => string.Equals(t, d.Type, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault() ?? throw NoDevice();
                await spotify.PlayAsync(token, trackId, device.Id, ct);
            }
        }, cancellationToken);
    }

    private static Task<IResult> PauseAsync(
        ClaimsPrincipal user, SpotifyPlayerAccess access, SpotifyClient spotify, IMemoryCache cache,
        IOptions<SpotifyOptions> options, CancellationToken cancellationToken) =>
        CommandAsync(user, access, cache, options, (token, ct) => spotify.PauseAsync(token, ct), cancellationToken);

    private static async Task<IResult> CommandAsync(
        ClaimsPrincipal user, SpotifyPlayerAccess access, IMemoryCache cache, IOptions<SpotifyOptions> options,
        Func<string, CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        if (!options.Value.IsEnabled)
        {
            throw SpotifyConnections.Unavailable("Spotify is not set up on this server.");
        }

        var userId = user.UserId();
        var connection = await access.LoadConnectionAsync(userId, cancellationToken) ?? throw SpotifyConnections.NotConnected();
        if (!connection.CanUsePlayer)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "spotify_reauth_required", "Spotify needs to be connected again",
                "Connect Spotify again to let the app play songs.");
        }

        await Spotify(() => access.CallAsync(userId, connection, async token =>
        {
            await command(token, cancellationToken);
            return true;
        }, cancellationToken));
        // The next read shows the change instead of a cached state.
        cache.Remove(StateKey(userId));
        return Results.NoContent();
    }

    // Spotify's refusals as this API's problem codes.
    private static async Task<T> Spotify<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (SpotifyRequestException e) when (e.IsNoActiveDevice)
        {
            throw NoDevice();
        }
        catch (SpotifyRequestException e) when (e.IsPremiumRequired)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "spotify_premium_required", "Spotify Premium is needed",
                "Spotify only lets apps control playback for Premium accounts. Open the song in Spotify instead.");
        }
        catch (SpotifyRequestException)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "spotify_player_refused", "Spotify didn't do that",
                "Spotify didn't accept that right now. Try it in the Spotify app.");
        }
        catch (SpotifyUnavailableException e)
        {
            throw SpotifyConnections.Unavailable("Spotify did not respond. Try again shortly.", e.RetryAfter);
        }
    }

    private static ApiProblemException NoDevice() =>
        new(StatusCodes.Status409Conflict, "spotify_no_device", "Spotify isn't open",
            "Open Spotify on your phone or computer, then try again.");

    [GeneratedRegex("^[0-9A-Za-z]{22}$")]
    private static partial Regex TrackIdPattern();
}
