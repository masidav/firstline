using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FandomScore.Api.Features.Artists;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Infrastructure;
using FandomScore.Spotify;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Api.Features.Featured;

/// <summary>
/// Featured artist of the week (migration 0023, decided 2026-10-25): staff pick a tracked artist for a week (Monday
/// to Sunday, UTC) with a video (an uploaded MP4, or a YouTube or TikTok link) and a song on Spotify. Fans read the
/// current week's pick; an uploaded video is served through a short-lived signed link.
/// </summary>
public static partial class FeaturedEndpoints
{
    public const string VideoPurpose = "featured";

    private static readonly TimeSpan VideoLinkLifetime = TimeSpan.FromHours(6);

    public static void MapFeatured(this RouteGroupBuilder v1)
    {
        v1.MapGet("/featured", GetCurrentAsync);
        v1.MapGet("/featured/videos/{uploadId:guid}", GetVideoAsync).AllowAnonymous();
        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapGet("/featured", ListAsync);
        admin.MapPut("/featured/{weekStart}", PutAsync);
        admin.MapDelete("/featured/{weekStart}", DeleteAsync);
    }

    /// <summary><c>Kind</c>: upload, youtube, or tiktok. <c>EmbedUrl</c> plays it in a page; <c>ThumbnailUrl</c> for YouTube.</summary>
    public sealed record Video(string Kind, string Url, string? EmbedUrl, string? ThumbnailUrl);

    public sealed record Song(string SpotifyTrackId, string Title, string? AlbumTitle, string? CoverUrl, string SpotifyUrl);

    public sealed record FeaturedArtist(
        Guid Id, DateOnly WeekStart, DateOnly WeekEnd, ScoringEndpoints.ArtistRef Artist, string? Headline, Video Video, Song Song);

    public sealed record Current(FeaturedArtist? Featured);

    public sealed record FeaturedList(IReadOnlyList<FeaturedArtist> Items);

    private static async Task<IResult> GetCurrentAsync(HttpRequest request, NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            $"{Select} WHERE f.week_start = date_trunc('week', now() AT TIME ZONE 'UTC')::date AND a.status IN ('onboarding', 'active')");
        return Results.Ok(new Current(row is null ? null : ToFeatured(row, request, urls)));
    }

    // The last four weeks and everything scheduled.
    private static async Task<IResult> ListAsync(HttpRequest request, NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(
            $"{Select} WHERE f.week_start >= date_trunc('week', now() AT TIME ZONE 'UTC')::date - 28 ORDER BY f.week_start DESC");
        return Results.Ok(new FeaturedList([.. rows.Select(r => ToFeatured(r, request, urls))]));
    }

    // Creates or replaces the pick for the week starting weekStart (a Monday). Body: artist_id, headline?, either
    // video_url (YouTube or TikTok) or video_upload_id (a finished featured_video upload), and spotify_track (an id or
    // an open.spotify.com/track link). The song's title and cover come from Spotify.
    private static async Task<IResult> PutAsync(
        ClaimsPrincipal user, string weekStart, JsonElement body, HttpRequest request, NpgsqlDataSource db, SignedUrls urls,
        SpotifyClient spotify, IOptions<SpotifyOptions> options, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        if (!DateOnly.TryParseExact(weekStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var week)
            || week.DayOfWeek != DayOfWeek.Monday)
        {
            errors.Add(new FieldError("week_start", "Use the Monday the week starts on, as YYYY-MM-DD."));
        }

        Guid? artistId = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("artist_id", out var a) && a.ValueKind == JsonValueKind.String
                         && Guid.TryParse(a.GetString(), out var parsedArtist) ? parsedArtist : null;
        if (artistId is null)
        {
            errors.Add(new FieldError("artist_id", "Send a tracked artist's id."));
        }

        var headline = Text(body, "headline")?.Trim();
        if (headline is { Length: > 200 })
        {
            errors.Add(new FieldError("headline", "Use at most 200 characters."));
        }

        var videoUrl = Text(body, "video_url")?.Trim();
        Guid? uploadId = Text(body, "video_upload_id") is { } u && Guid.TryParse(u, out var parsedUpload) ? parsedUpload : null;
        (string Kind, string Url)? link = null;
        if ((videoUrl is { Length: > 0 }) == (uploadId is not null))
        {
            errors.Add(new FieldError("video_url", "Send a YouTube or TikTok link, or an uploaded video's id (one of the two)."));
        }
        else if (videoUrl is { Length: > 0 } && (link = ParseVideoLink(videoUrl)) is null)
        {
            errors.Add(new FieldError("video_url", "Use a YouTube or TikTok video link."));
        }

        var trackId = ParseSpotifyTrack(Text(body, "spotify_track"));
        if (trackId is null)
        {
            errors.Add(new FieldError("spotify_track", "Send a Spotify track link (open.spotify.com/track/...) or its 22-character id."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM artists WHERE id = @artistId AND status IN ('onboarding', 'active'))", new { artistId }))
        {
            throw ApiProblemException.Validation(new FieldError("artist_id", "Send a tracked artist's id."));
        }

        if (uploadId is not null && !await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM uploads WHERE id = @uploadId AND purpose = 'featured_video' AND uploaded_at IS NOT NULL)", new { uploadId }))
        {
            throw ApiProblemException.Validation(new FieldError("video_upload_id", "Upload the video first (POST /uploads with purpose featured_video)."));
        }

        ArtistEndpoints.RequireCatalog(options.Value, "Featuring a song needs Spotify, which is not set up on this server.");
        var track = await ArtistEndpoints.CallSpotifyAsync(() => spotify.GetTrackAsync(trackId!, cancellationToken), loggers)
                    ?? throw new ApiProblemException(StatusCodes.Status404NotFound, "spotify_track_not_found", "Song not found",
                        "Spotify has no track with this id.");

        await connection.ExecuteAsync(
            """
            INSERT INTO featured_artists (week_start, artist_id, headline, video_kind, video_url, video_upload_id, spotify_track_id,
                                          track_title, track_album_title, track_cover_url, created_by)
            VALUES (@week, @artistId, @headline, @kind, @url, @uploadId, @trackId, @title, @album, @cover, @me)
            ON CONFLICT ON CONSTRAINT featured_artists_one_a_week DO UPDATE
              SET artist_id = EXCLUDED.artist_id, headline = EXCLUDED.headline, video_kind = EXCLUDED.video_kind,
                  video_url = EXCLUDED.video_url, video_upload_id = EXCLUDED.video_upload_id,
                  spotify_track_id = EXCLUDED.spotify_track_id, track_title = EXCLUDED.track_title,
                  track_album_title = EXCLUDED.track_album_title, track_cover_url = EXCLUDED.track_cover_url
            """,
            new
            {
                week = week.ToDateTime(TimeOnly.MinValue), artistId, headline = string.IsNullOrEmpty(headline) ? null : headline,
                kind = link?.Kind ?? "upload", url = link?.Url, uploadId, trackId = track.Id, title = track.Name, album = track.AlbumName,
                cover = track.CoverUrl, me = user.UserId(),
            });
        var row = await connection.QuerySingleAsync<Row>($"{Select} WHERE f.week_start = @week", new { week = week.ToDateTime(TimeOnly.MinValue) });
        return Results.Ok(ToFeatured(row, request, urls));
    }

    private static async Task<IResult> DeleteAsync(string weekStart, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(weekStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var week))
        {
            throw ApiProblemException.Validation(new FieldError("week_start", "Use the Monday the week starts on, as YYYY-MM-DD."));
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM featured_artists WHERE week_start = @week", new { week = week.ToDateTime(TimeOnly.MinValue) });
        return Results.NoContent();
    }

    // An uploaded featured video, through a signed link (like submission files). Range requests let players seek.
    private static async Task<IResult> GetVideoAsync(
        Guid uploadId, long? exp, string? sig, NpgsqlDataSource db, SignedUrls urls, IFileStore files, HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (!urls.Verify(VideoPurpose, uploadId, exp, sig))
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "forbidden", "Link expired", "This link is invalid or has expired.");
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var key = await connection.ExecuteScalarAsync<string?>(
            "SELECT u.storage_key FROM uploads u WHERE u.id = @uploadId AND u.purpose = 'featured_video' AND EXISTS (SELECT 1 FROM featured_artists f WHERE f.video_upload_id = u.id)",
            new { uploadId });
        var content = key is null ? null : await files.ReadAsync(key, cancellationToken);
        if (content is null)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Video not found", "No featured video has this id.");
        }

        response.Headers.CacheControl = "private, max-age=3600";
        response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(content, "video/mp4", enableRangeProcessing: true);
    }

    /// <summary>A YouTube (watch, youtu.be, shorts) or TikTok video link, normalised; null for anything else.</summary>
    public static (string Kind, string Url)? ParseVideoLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return null;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        host = host.StartsWith("m.", StringComparison.OrdinalIgnoreCase) ? host[2..] : host;
        string? youtubeId = host.ToLowerInvariant() switch
        {
            "youtube.com" when uri.AbsolutePath == "/watch" => System.Web.HttpUtility.ParseQueryString(uri.Query)["v"],
            "youtube.com" when uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal) => uri.AbsolutePath["/shorts/".Length..].Trim('/'),
            "youtu.be" => uri.AbsolutePath.Trim('/'),
            _ => null,
        };
        if (youtubeId is not null)
        {
            return YouTubeId().IsMatch(youtubeId) ? ("youtube", $"https://www.youtube.com/watch?v={youtubeId}") : null;
        }

        if (host.ToLowerInvariant() is "tiktok.com" && TikTokPath().Match(uri.AbsolutePath) is { Success: true } tiktok)
        {
            return ("tiktok", $"https://www.tiktok.com/@{tiktok.Groups[1].Value}/video/{tiktok.Groups[2].Value}");
        }

        return host.ToLowerInvariant() is "vm.tiktok.com" or "vt.tiktok.com" && uri.AbsolutePath.Trim('/').Length > 0
            ? ("tiktok", $"https://{host.ToLowerInvariant()}{uri.AbsolutePath}")
            : null;
    }

    /// <summary>A Spotify track id from a 22-character id or an open.spotify.com/track link.</summary>
    public static string? ParseSpotifyTrack(string? value)
    {
        value = value?.Trim();
        if (value is null)
        {
            return null;
        }

        if (SpotifyId().IsMatch(value))
        {
            return value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase)
               && SpotifyTrackPath().Match(uri.AbsolutePath) is { Success: true } match
            ? match.Groups[1].Value
            : null;
    }

    private const string Select =
        """
        SELECT f.id AS Id, f.week_start AS WeekStart, a.id AS ArtistId, a.name AS ArtistName, a.image_url AS ArtistImageUrl,
               f.headline AS Headline, f.video_kind AS VideoKind, f.video_url AS VideoUrl, f.video_upload_id AS VideoUploadId,
               f.spotify_track_id AS SpotifyTrackId, f.track_title AS TrackTitle, f.track_album_title AS TrackAlbumTitle,
               f.track_cover_url AS TrackCoverUrl
        FROM featured_artists f JOIN artists a ON a.id = f.artist_id
        """;

    private static FeaturedArtist ToFeatured(Row r, HttpRequest request, SignedUrls urls)
    {
        var week = r.WeekStart;
        Video video;
        if (r.VideoKind == "upload")
        {
            var url = $"{SignedUrls.Origin(request)}/v1/featured/videos/{r.VideoUploadId}{urls.Sign(VideoPurpose, r.VideoUploadId!.Value, VideoLinkLifetime).Query}";
            video = new Video("upload", url, url, null);
        }
        else if (r.VideoKind == "youtube")
        {
            var id = System.Web.HttpUtility.ParseQueryString(new Uri(r.VideoUrl!).Query)["v"];
            video = new Video("youtube", r.VideoUrl!, $"https://www.youtube.com/embed/{id}", $"https://img.youtube.com/vi/{id}/hqdefault.jpg");
        }
        else
        {
            var videoId = TikTokPath().Match(new Uri(r.VideoUrl!).AbsolutePath) is { Success: true } m ? m.Groups[2].Value : null;
            video = new Video("tiktok", r.VideoUrl!, videoId is null ? null : $"https://www.tiktok.com/embed/v2/{videoId}", null);
        }

        return new FeaturedArtist(r.Id, week, week.AddDays(6), new ScoringEndpoints.ArtistRef(r.ArtistId, r.ArtistName, r.ArtistImageUrl),
            r.Headline, video, new Song(r.SpotifyTrackId, r.TrackTitle, r.TrackAlbumTitle, r.TrackCoverUrl,
                $"https://open.spotify.com/track/{r.SpotifyTrackId}"));
    }

    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex YouTubeId();

    [GeneratedRegex(@"^/@([A-Za-z0-9._]{1,40})/video/(\d{5,25})/?$")]
    private static partial Regex TikTokPath();

    [GeneratedRegex("^[0-9A-Za-z]{22}$")]
    private static partial Regex SpotifyId();

    [GeneratedRegex(@"^/(?:intl-[a-z]{2}/)?track/([0-9A-Za-z]{22})/?$")]
    private static partial Regex SpotifyTrackPath();

    private sealed record Row(
        Guid Id, DateOnly WeekStart, Guid ArtistId, string ArtistName, string? ArtistImageUrl, string? Headline, string VideoKind,
        string? VideoUrl, Guid? VideoUploadId, string SpotifyTrackId, string TrackTitle, string? TrackAlbumTitle, string? TrackCoverUrl);
}
