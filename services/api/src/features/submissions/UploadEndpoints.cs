using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using FandomScore.Storage;
using Npgsql;

namespace FandomScore.Api.Features.Submissions;

/// <summary>
/// openapi <c>POST /uploads</c> and the signed links behind it: <c>PUT /uploads/{id}/content</c> takes the bytes and
/// <c>GET /files/{id}</c> shows a stored file. Both links carry their own short-lived signature (like presigned
/// storage URLs), so they need no sign-in. Stored files have their metadata stripped (<see cref="ImageMetadata"/>).
/// Uploads always go through the API, so nothing is stored before the metadata (GPS location included) is gone.
/// Downloads redirect to a short-lived Cloud Storage link when the store can make one, so the API doesn't stream
/// the bytes (<see cref="RedirectToStorage"/>).
/// </summary>
public static class UploadEndpoints
{
    public const string UploadPurpose = "upload";
    public const string FilePurpose = "file";
    public const int MaxBytes = 15 * 1024 * 1024;

    /// <summary>Staff-uploaded featured-artist videos (migration 0023): MP4 up to 100 MB, stored as uploaded.</summary>
    public const int MaxVideoBytes = 100 * 1024 * 1024;

    public const string VideoPurpose = "featured_video";
    public const int DailyUploads = 50;

    private static readonly TimeSpan UploadLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan FileLinkLifetime = TimeSpan.FromHours(1);

    /// <summary>How long a redirect to Cloud Storage works; the API's own link (an hour) is the one apps hold on to.</summary>
    public static readonly TimeSpan StorageLinkLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A redirect to the file in storage, or null when the store can't sign links (local disk): then the caller serves
    /// the bytes. The redirect may be cached for less time than the storage link lives.
    /// </summary>
    public static async Task<IResult?> RedirectToStorage(IFileStore files, string key, HttpResponse response, CancellationToken cancellationToken)
    {
        if (await files.SignedReadUrlAsync(key, StorageLinkLifetime, cancellationToken) is not { } url)
        {
            return null;
        }

        response.Headers.CacheControl = "private, max-age=600";
        return Results.Redirect(url.ToString());
    }

    public static IServiceCollection AddUploads(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddFileStore(configuration, environment.ContentRootPath);
        services.AddSingleton<SignedUrls>();
        return services;
    }

    public static void MapUploads(this RouteGroupBuilder v1)
    {
        v1.MapPost("/uploads", CreateAsync);
        v1.MapPut("/uploads/{uploadId:guid}/content", PutContentAsync).AllowAnonymous();
        v1.MapGet("/files/{fileId:guid}", GetFileAsync).AllowAnonymous();
    }

    public sealed record UploadIntent(Guid UploadId, string Method, string Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

    /// <summary>A signed, absolute link to one stored submission file.</summary>
    public static string FileUrl(HttpRequest request, SignedUrls urls, Guid fileId) =>
        $"{SignedUrls.Origin(request)}/v1/files/{fileId}{urls.Sign(FilePurpose, fileId, FileLinkLifetime).Query}";

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal user, HttpRequest request, JsonElement body, NpgsqlDataSource db, SignedUrls urls, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var purpose = StringField(body, "purpose");
        var video = purpose == VideoPurpose;
        if (purpose != "submission" && !video)
        {
            errors.Add(new FieldError("purpose", purpose == "avatar" ? "Avatars aren't supported yet." : "Use submission."));
        }

        var mimeType = StringField(body, "mime_type");
        if (video ? mimeType != "video/mp4" : mimeType is not ("image/jpeg" or "image/png"))
        {
            errors.Add(new FieldError("mime_type", video ? "Send an MP4 video." : "Send a JPEG or PNG photo (convert HEIC or WebP first)."));
        }

        int bytes = 0;
        if (!(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number
              && b.TryGetInt32(out bytes) && bytes >= 1 && bytes <= (video ? MaxVideoBytes : MaxBytes)))
        {
            errors.Add(new FieldError("bytes", video ? "Videos can be up to 100 MB." : "Photos can be up to 15 MB."));
        }

        byte[]? declaredSha = null;
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("sha256", out var sha) && sha.ValueKind != JsonValueKind.Null)
        {
            var hex = sha.ValueKind == JsonValueKind.String ? sha.GetString() : null;
            if (hex is { Length: 64 } && hex.All(c => char.IsAsciiDigit(c) || char.IsAsciiHexDigitLower(c)))
            {
                declaredSha = Convert.FromHexString(hex);
            }
            else
            {
                errors.Add(new FieldError("sha256", "Send the file's SHA-256 as 64 lowercase hex characters."));
            }
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (video && !await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM users WHERE id = @userId AND is_staff AND deleted_at IS NULL)", new { userId }))
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "forbidden", "Staff only", "Only staff upload featured-artist videos.");
        }

        var today = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM uploads WHERE user_id = @userId AND created_at >= now() - interval '1 day'", new { userId });
        if (today >= DailyUploads)
        {
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many uploads",
                $"You can upload {DailyUploads} photos a day. Try again tomorrow.") { RetryAfter = TimeSpan.FromHours(1) };
        }

        var (uploadId, expiresAt) = await connection.QuerySingleAsync<(Guid, DateTime)>(
            """
            INSERT INTO uploads (user_id, purpose, mime_type, declared_bytes, declared_sha256, expires_at)
            VALUES (@userId, @purpose, @mimeType, @bytes, @declaredSha, now() + @lifetime) RETURNING id, expires_at
            """,
            new { userId, purpose, mimeType, bytes, declaredSha, lifetime = UploadLifetime });
        // The link outlives the slot a little, so a late upload hears "expired" (409) rather than "invalid link".
        var (query, _) = urls.Sign(UploadPurpose, uploadId, UploadLifetime + TimeSpan.FromMinutes(10));
        return Results.Created((string?)null, new UploadIntent(uploadId, "PUT",
            $"{SignedUrls.Origin(request)}/v1/uploads/{uploadId}/content{query}",
            new Dictionary<string, string> { ["Content-Type"] = mimeType! }, new DateTimeOffset(expiresAt, TimeSpan.Zero)));
    }

    private static async Task<IResult> PutContentAsync(
        Guid uploadId, long? exp, string? sig, HttpRequest request, NpgsqlDataSource db, SignedUrls urls, IFileStore files,
        CancellationToken cancellationToken)
    {
        if (!urls.Verify(UploadPurpose, uploadId, exp, sig))
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "forbidden", "Link expired",
                "This upload link is invalid or has expired. Start the upload again.");
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var upload = await connection.QuerySingleOrDefaultAsync<UploadRow>(
            """
            SELECT mime_type AS MimeType, declared_bytes AS DeclaredBytes, declared_sha256 AS DeclaredSha256,
                   uploaded_at IS NOT NULL AS Uploaded, expires_at < now() AS Expired
            FROM uploads WHERE id = @uploadId
            """,
            new { uploadId })
            ?? throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Upload not found", "No upload has this id.");
        if (upload.Uploaded || upload.Expired)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, upload.Expired ? "upload_expired" : "conflict",
                upload.Expired ? "Upload expired" : "Already uploaded",
                upload.Expired ? "This upload has expired. Start again." : "This upload already has its file.");
        }

        if (upload.MimeType == "video/mp4"
            && request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxVideoBytes + 1L;   // Kestrel's default is 30 MB
        }

        // Read at most the declared size (plus one byte to notice a bigger body).
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > upload.DeclaredBytes)
            {
                throw ApiProblemException.Validation(new FieldError("body", "The file is bigger than declared."));
            }
        }

        var original = buffer.ToArray();
        if (original.Length == 0)
        {
            throw ApiProblemException.Validation(new FieldError("body", "The file is empty."));
        }

        if (upload.DeclaredSha256 is { } declared && !SHA256.HashData(original).AsSpan().SequenceEqual(declared))
        {
            throw ApiProblemException.Validation(new FieldError("body", "The file doesn't match the declared SHA-256."));
        }

        // Videos are stored as uploaded (staff-made promo clips; photo metadata stripping doesn't apply). An MP4
        // starts with a box size and the "ftyp" box.
        var stripped = upload.MimeType == "video/mp4"
            ? (original.Length > 12 && original.AsSpan(4, 4).SequenceEqual("ftyp"u8)
                ? original
                : throw ApiProblemException.Validation(new FieldError("body", "This isn't a valid MP4 video.")))
            : ImageMetadata.Strip(original, upload.MimeType)
              ?? throw ApiProblemException.Validation(new FieldError("body", $"This isn't a valid {(upload.MimeType == "image/png" ? "PNG" : "JPEG")} image."));
        // Featured videos are staff media, not proof: they live outside quarantine, which deletes everything at 30 days.
        var key = upload.MimeType == "video/mp4" ? $"media/{uploadId}" : $"quarantine/{uploadId}";
        await files.PutAsync(key, stripped, cancellationToken);
        var updated = await connection.ExecuteAsync(
            """
            UPDATE uploads SET storage_key = @key, sha256 = @sha, bytes = @bytes, uploaded_at = now()
            WHERE id = @uploadId AND uploaded_at IS NULL
            """,
            new { uploadId, key, sha = SHA256.HashData(stripped), bytes = stripped.Length });
        if (updated == 0)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already uploaded", "This upload already has its file.");
        }

        return Results.NoContent();
    }

    private static async Task<IResult> GetFileAsync(
        Guid fileId, long? exp, string? sig, NpgsqlDataSource db, SignedUrls urls, IFileStore files, HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (!urls.Verify(FilePurpose, fileId, exp, sig))
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "forbidden", "Link expired", "This link is invalid or has expired.");
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var file = await connection.QuerySingleOrDefaultAsync<(string StorageKey, string MimeType)>(
            "SELECT storage_key, mime_type FROM submission_files WHERE id = @fileId", new { fileId });
        if (file.StorageKey is not null && await RedirectToStorage(files, file.StorageKey, response, cancellationToken) is { } redirect)
        {
            return redirect;
        }

        var content = file.StorageKey is null ? null : await files.ReadAsync(file.StorageKey, cancellationToken);
        if (content is null)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "File not found", "No file has this id.");
        }

        response.Headers.CacheControl = "private, max-age=3600";
        response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(content, file.MimeType);
    }

    private static string? StringField(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record UploadRow(string MimeType, int DeclaredBytes, byte[]? DeclaredSha256, bool Uploaded, bool Expired);
}
