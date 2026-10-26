using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FandomScore.Spotify;

/// <summary>
/// The fan's Spotify player (Web API "Player" endpoints) for the app's now-playing bar and play buttons. Reading
/// needs scope <see cref="SpotifyOptions.PlaybackReadScope"/>, commands need
/// <see cref="SpotifyOptions.PlaybackControlScope"/> and Spotify Premium. Commands are not retried on server errors
/// (a retried "next" could skip twice); short rate limits are, since Spotify did nothing.
/// </summary>
public sealed partial class SpotifyClient
{
    /// <summary>What's playing, or null when Spotify is idle (it answers 204).</summary>
    public async Task<SpotifyPlayback?> GetPlaybackAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => PlayerRequest(HttpMethod.Get, "me/player?additional_types=track", accessToken),
            "playback state", retryServerErrors: true, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        await ThrowForPlayerErrorAsync(response, "playback state", cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var body = JsonSerializer.Deserialize<PlaybackDto>(text, Json);
        if (body is null)
        {
            return null;
        }

        var track = body.CurrentlyPlayingType is null or "track" ? ToTrackDetail(body.Item) : null;
        return new SpotifyPlayback(body.IsPlaying, body.ProgressMs, body.Item?.DurationMs, track, ToDevice(body.Device));
    }

    /// <summary>Devices the fan's Spotify can play on right now.</summary>
    public async Task<IReadOnlyList<SpotifyDevice>> GetDevicesAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => PlayerRequest(HttpMethod.Get, "me/player/devices", accessToken),
            "devices", retryServerErrors: true, cancellationToken);
        await ThrowForPlayerErrorAsync(response, "devices", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<DevicesDto>(Json, cancellationToken);
        return [.. (body?.Devices ?? []).Select(ToDevice).OfType<SpotifyDevice>()];
    }

    /// <summary>
    /// Plays a track from the start, or resumes when <paramref name="spotifyTrackId"/> is null. A null
    /// <paramref name="deviceId"/> means the active device; Spotify answers 404 when there is none.
    /// </summary>
    public Task PlayAsync(string accessToken, string? spotifyTrackId, string? deviceId, CancellationToken cancellationToken)
    {
        var path = deviceId is null ? "me/player/play" : $"me/player/play?device_id={Uri.EscapeDataString(deviceId)}";
        return CommandAsync(HttpMethod.Put, path, accessToken, "play", cancellationToken,
            spotifyTrackId is null ? null : new { uris = new[] { $"spotify:track:{spotifyTrackId}" } });
    }

    public Task PauseAsync(string accessToken, CancellationToken cancellationToken) =>
        CommandAsync(HttpMethod.Put, "me/player/pause", accessToken, "pause", cancellationToken);

    /// <summary>Skips to the next track, or back to the previous one.</summary>
    public Task SkipAsync(string accessToken, bool forward, CancellationToken cancellationToken) =>
        CommandAsync(HttpMethod.Post, forward ? "me/player/next" : "me/player/previous", accessToken,
            forward ? "next" : "previous", cancellationToken);

    private async Task CommandAsync(
        HttpMethod method, string path, string accessToken, string label, CancellationToken cancellationToken, object? body = null)
    {
        using var response = await SendAsync(() =>
        {
            var request = PlayerRequest(method, path, accessToken);
            // Spotify wants a length on PUT and POST even without a body.
            request.Content = body is null ? new ByteArrayContent([]) : JsonContent.Create(body, options: Json);
            return request;
        }, label, retryServerErrors: false, cancellationToken);
        await ThrowForPlayerErrorAsync(response, label, cancellationToken);
    }

    private static HttpRequestMessage PlayerRequest(HttpMethod method, string pathAndQuery, string accessToken)
    {
        var request = new HttpRequestMessage(method, new Uri(ApiBase, pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    // Player errors look like {"error": {"status": 404, "message": "...", "reason": "NO_ACTIVE_DEVICE"}}.
    private static async Task ThrowForPlayerErrorAsync(HttpResponseMessage response, string label, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SpotifyAuthorizationException($"Spotify rejected the access token ({label}).");
        }

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? reason = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<PlayerErrorDto>(Json, cancellationToken);
            reason = error?.Error?.Reason;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            // No JSON body: the status says enough.
        }

        throw new SpotifyRequestException((int)response.StatusCode, reason);
    }

    private static SpotifyDevice? ToDevice(DeviceDto? device) =>
        device is { Name: { } name }
            ? new SpotifyDevice(device.Id, name, device.Type ?? "Unknown", device.IsActive, device.IsRestricted)
            : null;

    private sealed record PlaybackDto(bool IsPlaying, int? ProgressMs, string? CurrentlyPlayingType, TrackDto? Item, DeviceDto? Device);

    private sealed record DeviceDto(string? Id, string? Name, string? Type, bool IsActive, bool IsRestricted);

    private sealed record DevicesDto(IReadOnlyList<DeviceDto>? Devices);

    private sealed record PlayerErrorDto(PlayerErrorBodyDto? Error);

    private sealed record PlayerErrorBodyDto(int? Status, string? Message, string? Reason);
}
