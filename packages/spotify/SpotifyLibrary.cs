using System.Net.Http.Headers;

namespace FandomScore.Spotify;

/// <summary>
/// The fan's Spotify library (Web API "Tracks" and "Albums" endpoints) for the app's Like buttons: whether songs and
/// albums are saved, and saving or removing them. Checks need scope <c>user-library-read</c>; changes need
/// <see cref="SpotifyOptions.LibraryModifyScope"/>. Spotify allows 50 track ids and 20 album ids per request. Saves
/// and removals are idempotent, so they are retried on server errors.
/// </summary>
public sealed partial class SpotifyClient
{
    public const int MaxLibraryTrackIds = 50;
    public const int MaxLibraryAlbumIds = 20;

    /// <summary>Which of <paramref name="spotifyTrackIds"/> are in the fan's Liked Songs, in the same order.</summary>
    public Task<IReadOnlyList<bool>> GetSavedTracksAsync(string accessToken, IReadOnlyList<string> spotifyTrackIds, CancellationToken cancellationToken) =>
        ContainsAsync(accessToken, "me/tracks/contains", spotifyTrackIds, "saved tracks check", cancellationToken);

    /// <summary>Which of <paramref name="spotifyAlbumIds"/> the fan has saved, in the same order.</summary>
    public Task<IReadOnlyList<bool>> GetSavedAlbumsAsync(string accessToken, IReadOnlyList<string> spotifyAlbumIds, CancellationToken cancellationToken) =>
        ContainsAsync(accessToken, "me/albums/contains", spotifyAlbumIds, "saved albums check", cancellationToken);

    public Task SaveTrackAsync(string accessToken, string spotifyTrackId, CancellationToken cancellationToken) =>
        LibraryAsync(HttpMethod.Put, "me/tracks", spotifyTrackId, accessToken, "save track", cancellationToken);

    public Task RemoveTrackAsync(string accessToken, string spotifyTrackId, CancellationToken cancellationToken) =>
        LibraryAsync(HttpMethod.Delete, "me/tracks", spotifyTrackId, accessToken, "remove track", cancellationToken);

    public Task SaveAlbumAsync(string accessToken, string spotifyAlbumId, CancellationToken cancellationToken) =>
        LibraryAsync(HttpMethod.Put, "me/albums", spotifyAlbumId, accessToken, "save album", cancellationToken);

    public Task RemoveAlbumAsync(string accessToken, string spotifyAlbumId, CancellationToken cancellationToken) =>
        LibraryAsync(HttpMethod.Delete, "me/albums", spotifyAlbumId, accessToken, "remove album", cancellationToken);

    private async Task<IReadOnlyList<bool>> ContainsAsync(
        string accessToken, string path, IReadOnlyList<string> ids, string label, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var saved = await GetAsync<bool[]>(accessToken, $"{path}?ids={Uri.EscapeDataString(string.Join(',', ids))}", label, cancellationToken);
        return saved.Length == ids.Count
            ? saved
            : throw new SpotifyUnavailableException($"Spotify answered the {label} with {saved.Length} values for {ids.Count} ids.");
    }

    private async Task LibraryAsync(
        HttpMethod method, string path, string id, string accessToken, string label, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, new Uri(ApiBase, $"{path}?ids={Uri.EscapeDataString(id)}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            // Spotify wants a length on PUT and DELETE even without a body.
            request.Content = new ByteArrayContent([]);
            return request;
        }, label, retryServerErrors: true, cancellationToken);
        await ThrowForPlayerErrorAsync(response, label, cancellationToken);
    }
}
