using Dapper;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using FandomScore.Storage;
using Npgsql;
using SkiaSharp;

namespace FandomScore.Api.Features.Users;

/// <summary>
/// Profile pictures (migration 0052). A fan uploads a photo the way they upload proof (<c>POST /uploads</c> with
/// purpose <c>avatar</c>); <see cref="Process"/> turns it upright, crops it square, scales it to at most
/// <see cref="Size"/> px, and re-encodes it as a JPEG (so no metadata survives), and it is stored under
/// <c>media/avatars/</c>. <c>PATCH /me</c> then names the upload (<c>avatar_upload_id</c>) and the fan's
/// <c>avatar_url</c> becomes <c>GET /avatars/{uploadId}</c>: a stable link without a signature, because the link
/// is handed to every fan who sees the name and image tags can't send a token. It is safe because the id is
/// unguessable and the link only works while the upload is somebody's current picture: changing or removing the
/// picture deletes the old upload row, so its link answers 404 at once and storage.cleanup removes the file.
/// </summary>
public static class Avatars
{
    public const string Purpose = "avatar";

    /// <summary>The longest side of a stored picture, in pixels.</summary>
    public const int Size = 512;

    public const string UploadRule =
        "Upload a picture first (POST /uploads with purpose avatar) and send its upload_id, or null to remove the picture.";

    private const int JpegQuality = 85;

    /// <summary>Bigger pictures aren't decoded (memory); the app scales photos down before uploading them.</summary>
    private const long MaxPixels = 40_000_000;

    public static string StorageKey(Guid uploadId) => $"media/avatars/{uploadId}";

    public static string Url(HttpRequest request, Guid uploadId) => $"{SignedUrls.Origin(request)}/v1/avatars/{uploadId}";

    public static void MapAvatars(this RouteGroupBuilder v1) => v1.MapGet("/avatars/{uploadId:guid}", GetAsync).AllowAnonymous();

    /// <summary>
    /// The uploaded photo as an upright, square JPEG of at most <see cref="Size"/> px a side (the middle of the photo),
    /// or null when the bytes aren't a readable JPEG or PNG, or the picture is too big to decode.
    /// </summary>
    public static byte[]? Process(byte[] original)
    {
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            return null;
        }

        // Decode no bigger than needed: JPEG decoders scale down for free, in eighths, rounding down (PNG decodes at
        // full size). Ask for the next eighth up, so the short side still has Size pixels to work with.
        var wanted = Math.Min(1f, MathF.Ceiling(Size * 8f / Math.Min(codec.Info.Width, codec.Info.Height)) / 8f);
        var dimensions = codec.GetScaledDimensions(wanted);
        using var source = SKBitmap.Decode(codec, new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (source is null)
        {
            return null;
        }

        var rotated = codec.EncodedOrigin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftTop;
        var (width, height) = rotated ? (source.Height, source.Width) : (source.Width, source.Height);
        var side = Math.Min(width, height);
        var target = Math.Min(side, Size);
        var scale = (float)target / side;
        using var square = new SKBitmap(target, target, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(square))
        {
            canvas.Clear(SKColors.White);   // behind a transparent PNG
            // Centre the upright picture on the square, then apply the camera orientation so it's drawn upright.
            canvas.Translate(target / 2f, target / 2f);
            canvas.Scale(scale);
            canvas.Translate(-width / 2f, -height / 2f);
            canvas.Concat(Upright(codec.EncodedOrigin, width, height));
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using var result = SKImage.FromBitmap(square);
        using var jpeg = result.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return jpeg?.ToArray();
    }

    /// <summary>The transform that turns the decoded pixels upright, for a camera orientation (EXIF) the codec reports.</summary>
    private static SKMatrix Upright(SKEncodedOrigin origin, int uprightWidth, int uprightHeight) => origin switch
    {
        SKEncodedOrigin.TopRight => SKMatrix.CreateScale(-1, 1, uprightWidth / 2f, 0),
        SKEncodedOrigin.BottomRight => SKMatrix.CreateRotationDegrees(180, uprightWidth / 2f, uprightHeight / 2f),
        SKEncodedOrigin.BottomLeft => SKMatrix.CreateScale(1, -1, 0, uprightHeight / 2f),
        SKEncodedOrigin.LeftTop => SKMatrix.Concat(SKMatrix.CreateScale(-1, 1, uprightWidth / 2f, 0), SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(uprightWidth, 0))),
        SKEncodedOrigin.RightTop => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(uprightWidth, 0)),
        SKEncodedOrigin.RightBottom => SKMatrix.CreateScale(-1, 1, uprightWidth / 2f, 0).PreConcat(SKMatrix.CreateRotationDegrees(-90).PostConcat(SKMatrix.CreateTranslation(0, uprightHeight))),
        SKEncodedOrigin.LeftBottom => SKMatrix.CreateRotationDegrees(-90).PostConcat(SKMatrix.CreateTranslation(0, uprightHeight)),
        _ => SKMatrix.Identity,
    };

    // The link is public (no sign-in) and stable; it works only while the upload is a live fan's current picture.
    private static async Task<IResult> GetAsync(
        Guid uploadId, NpgsqlDataSource db, IFileStore files, HttpResponse response, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var key = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT up.storage_key FROM uploads up JOIN users u ON u.avatar_upload_id = up.id
            WHERE up.id = @uploadId AND up.purpose = 'avatar' AND u.deleted_at IS NULL
            """,
            new { uploadId });
        if (key is not null && await UploadEndpoints.RedirectToStorage(files, key, response, cancellationToken) is { } redirect)
        {
            return redirect;
        }

        var content = key is null ? null : await files.ReadAsync(key, cancellationToken);
        if (content is null)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Picture not found", "No fan has this picture any more.");
        }

        // A picture never changes under its link (a new one gets a new id), so it may be cached for a day.
        response.Headers.CacheControl = "private, max-age=86400";
        response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(content, "image/jpeg");
    }
}
