using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.SpotifySync;
using FandomScore.Api.Infrastructure;
using FandomScore.Spotify;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;

namespace FandomScore.Api.Features.Artists;

/// <summary>
/// openapi <c>GET /artists</c>, <c>/artists/search</c>, and <c>POST /artists</c>. Spotify is called with the app's own token (client
/// credentials), so users don't need to connect Spotify first. Untracked search results are never stored, and an
/// added artist's name and image come from Spotify, never from the client. See docs/adr/0005-spotify-integration.md.
/// </summary>
public static partial class ArtistEndpoints
{
    public const string SearchRateLimit = "artist-search";

    private const int SearchPermitsPerMinute = 30;

    public static IServiceCollection AddArtists(this IServiceCollection services) =>
        services.Configure<RateLimiterOptions>(limiter => limiter.AddPolicy(SearchRateLimit, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = SearchPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })));

    public static void MapArtists(this RouteGroupBuilder v1)
    {
        v1.MapGet("/artists", ListAsync);
        v1.MapGet("/artists/search", SearchAsync).RequireRateLimiting(SearchRateLimit);
        v1.MapPost("/artists", TrackAsync);
    }

    public sealed record TrackArtistRequest(string? SpotifyArtistId);

    public sealed record ArtistSearchResult(
        string SpotifyArtistId, string Name, string? ImageUrl, IReadOnlyList<string> Genres, bool Tracked, Guid? ArtistId,
        string? Status, int? ReleaseCount);

    public sealed record ArtistSearchResults(IReadOnlyList<ArtistSearchResult> Items);

    public sealed record ArtistPage(IReadOnlyList<Artist> Items, string? NextCursor);

    /// <summary>openapi Artist. <see cref="MyScore"/> stays null until scoring exists (Phase 5).</summary>
    public sealed record Artist(
        Guid Id, string Name, string SpotifyArtistId, string? ImageUrl, string? WebsiteUrl, string? MerchUrl, string Status,
        bool AddedByMe, DateTime TrackedAt, DateTime? ActivatedAt, object? MyScore);

    // Tracked artists only; this never calls Spotify. Paused artists are never listed. The cursor is an opaque offset,
    // which is fine while the catalog is small.
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, string? q, string? status, string? sort, int? limit, string? cursor, NpgsqlDataSource db,
        CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        var errors = new List<FieldError>();
        if (query.Length > 100)
        {
            errors.Add(new FieldError("q", "Send at most 100 characters."));
        }

        string[]? statuses = (status ?? "active") switch
        {
            "active" => ["active"],
            "onboarding" => ["onboarding"],
            "all" => ["active", "onboarding"],
            _ => null,
        };
        if (statuses is null)
        {
            errors.Add(new FieldError("status", "Use active, onboarding, or all."));
        }

        var orderBy = (sort ?? "fans") switch
        {
            "fans" => "(SELECT count(*) FROM user_artist_scores s WHERE s.artist_id = a.id) DESC, lower(a.name), a.id",
            "name" => "lower(a.name), a.id",
            "recent" => "a.tracked_at DESC, a.id",
            _ => null,
        };
        if (orderBy is null)
        {
            errors.Add(new FieldError("sort", "Use fans, name, or recent."));
        }

        if (limit is < 1 or > 100)
        {
            errors.Add(new FieldError("limit", "Use a limit from 1 to 100."));
        }

        var offset = 0;
        if (cursor is not null && !TryReadCursor(cursor, out offset))
        {
            errors.Add(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        // Matches the start of the name or of any word in it ("beat" finds "The Beatles").
        var escaped = query.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
        var pageSize = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ArtistRow>(
            $"""
             SELECT {ArtistColumns}
             FROM artists a
             WHERE a.status = ANY(@statuses::artist_status[])
               AND (@query = '' OR a.name ILIKE @prefix OR a.name ILIKE @wordPrefix)
             ORDER BY {orderBy}
             LIMIT @take OFFSET @offset
             """,
            new
            {
                userId = user.UserId(), statuses, query, prefix = escaped + "%", wordPrefix = "% " + escaped + "%",
                take = pageSize + 1, offset,
            })).ToList();

        var next = rows.Count > pageSize ? WriteCursor(offset + pageSize) : null;
        return Results.Ok(new ArtistPage([.. rows.Take(pageSize).Select(ToArtist)], next));
    }

    private static async Task<IResult> SearchAsync(
        string? q, int? limit, NpgsqlDataSource db, SpotifyClient spotify, IOptions<SpotifyOptions> options,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        var errors = new List<FieldError>();
        if (query.Length is < 2 or > 100)
        {
            errors.Add(new FieldError("q", "Send 2 to 100 characters to search for."));
        }

        if (limit is < 1 or > SpotifyClient.MaxSearchLimit)
        {
            errors.Add(new FieldError("limit", $"Use a limit from 1 to {SpotifyClient.MaxSearchLimit}."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        RequireCatalog(options.Value, "Artist search is not set up on this server.");
        var found = await CallSpotifyAsync(
            () => spotify.SearchArtistsAsync(query, limit ?? SpotifyClient.MaxSearchLimit, cancellationToken), loggers);

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var tracked = (await connection.QueryAsync<TrackedRow>(
                "SELECT spotify_artist_id AS SpotifyArtistId, id AS Id, status::text AS Status FROM artists WHERE spotify_artist_id = ANY(@ids)",
                new { ids = found.Select(a => a.Id).ToArray() }))
            .ToDictionary(row => row.SpotifyArtistId);

        var releaseCounts = await CountSameNameReleasesAsync(found, spotify, loggers, cancellationToken);
        return Results.Ok(new ArtistSearchResults([.. GroupSameNames(found, releaseCounts).Select(artist =>
        {
            var row = tracked.GetValueOrDefault(artist.Id);
            return new ArtistSearchResult(artist.Id, artist.Name, artist.ImageUrl, artist.Genres, row is not null, row?.Id,
                row?.Status, releaseCounts.GetValueOrDefault(artist.Id));
        })]));
    }

    // Spotify gives apps no follower counts or popularity, so artists that share a name ("Radiohead" and an
    // impostor) are told apart by release count. Only same-named results cost a call; a failed count is left out.
    private static async Task<Dictionary<string, int?>> CountSameNameReleasesAsync(
        IReadOnlyList<SpotifyArtist> found, SpotifyClient spotify, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var sameName = found.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g);
        var counts = await Task.WhenAll(sameName.Select(async artist =>
        {
            try
            {
                return (artist.Id, Count: await spotify.CountArtistReleasesAsync(artist.Id, cancellationToken));
            }
            catch (Exception e) when (e is SpotifyUnavailableException or SpotifyRequestException or SpotifyAuthorizationException)
            {
                LogReleaseCountFailed(loggers.CreateLogger(typeof(ArtistEndpoints)), artist.Id, e.Message);
                return (artist.Id, Count: (int?)null);
            }
        }));
        return counts.ToDictionary(c => c.Id, c => c.Count);
    }

    // Keeps Spotify's order, but puts artists that share a name together, most releases first, where the first of
    // them was.
    private static IEnumerable<SpotifyArtist> GroupSameNames(IReadOnlyList<SpotifyArtist> found, Dictionary<string, int?> releaseCounts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artist in found)
        {
            if (!seen.Add(artist.Name))
            {
                continue;
            }

            foreach (var sameName in found.Where(a => string.Equals(a.Name, artist.Name, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(a => releaseCounts.GetValueOrDefault(a.Id) ?? -1))
            {
                yield return sameName;
            }
        }
    }

    // An artist already tracked is returned without calling Spotify. Otherwise the name and image are fetched from
    // Spotify and track_artist() adds it (uniqueness and the daily limit). It starts as onboarding.
    private static async Task<IResult> TrackAsync(
        ClaimsPrincipal user, TrackArtistRequest request, NpgsqlDataSource db, SpotifyClient spotify,
        IOptions<SpotifyOptions> options, TimeProvider time, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (request.SpotifyArtistId is not { } spotifyArtistId || !SpotifyIdPattern().IsMatch(spotifyArtistId))
        {
            throw ApiProblemException.Validation(new FieldError("spotify_artist_id",
                "Send the 22-character Spotify artist id from search."));
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (await LoadAsync(connection, userId, spotifyArtistId) is { } existing)
        {
            return Results.Ok(existing);
        }

        RequireCatalog(options.Value, "Adding artists is not set up on this server.");
        var artist = await CallSpotifyAsync(() => spotify.GetArtistAsync(spotifyArtistId, cancellationToken), loggers)
                     ?? throw new ApiProblemException(StatusCodes.Status404NotFound, "spotify_artist_not_found",
                         "Artist not found", "Spotify has no artist with this id.");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        TrackResult result;
        try
        {
            result = await connection.QuerySingleAsync<TrackResult>(
                "SELECT o_artist_id AS ArtistId, o_created AS Created FROM track_artist(@userId, @spotifyArtistId, @name)",
                new { userId, spotifyArtistId, name = artist.Name }, transaction);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                          && e.MessageText == "daily artist add limit reached")
        {
            var now = time.GetUtcNow();
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "artist_add_limit", "Daily artist limit reached",
                "You have added as many new artists as allowed today. Try again after midnight UTC.")
            {
                RetryAfter = now.UtcDateTime.Date.AddDays(1) - now.UtcDateTime,
            };
        }

        if (result.Created)
        {
            await connection.ExecuteAsync("UPDATE artists SET image_url = @imageUrl WHERE id = @id",
                new { imageUrl = artist.ImageUrl, id = result.ArtistId }, transaction);
        }

        var added = await LoadAsync(connection, userId, spotifyArtistId, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result.Created ? Results.Created($"/v1/artists/{added!.Id}", added) : Results.Ok(added);
    }

    private static async Task<Artist?> LoadAsync(
        NpgsqlConnection connection, Guid userId, string spotifyArtistId, NpgsqlTransaction? transaction = null)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ArtistRow>(
            $"SELECT {ArtistColumns} FROM artists a WHERE a.spotify_artist_id = @spotifyArtistId",
            new { userId, spotifyArtistId }, transaction);
        return row is null ? null : ToArtist(row);
    }

    // Needs @userId for added_by_me.
    private const string ArtistColumns =
        """
        a.id AS Id, a.name AS Name, a.spotify_artist_id AS SpotifyArtistId, a.image_url AS ImageUrl,
        a.website_url AS WebsiteUrl, a.merch_url AS MerchUrl, a.status::text AS Status,
        COALESCE(a.added_by = @userId, false) AS AddedByMe, a.tracked_at AS TrackedAt, a.activated_at AS ActivatedAt
        """;

    private static Artist ToArtist(ArtistRow row) =>
        new(row.Id, row.Name, row.SpotifyArtistId, row.ImageUrl, row.WebsiteUrl, row.MerchUrl, row.Status, row.AddedByMe,
            row.TrackedAt, row.ActivatedAt, null);

    private static string WriteCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static bool TryReadCursor(string cursor, out int offset)
    {
        offset = 0;
        try
        {
            return int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), NumberStyles.None,
                CultureInfo.InvariantCulture, out offset);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void RequireCatalog(SpotifyOptions options, string detail)
    {
        if (!options.IsCatalogEnabled)
        {
            throw SpotifyConnections.Unavailable(detail);
        }
    }

    private static async Task<T> CallSpotifyAsync<T>(Func<Task<T>> call, ILoggerFactory loggers)
    {
        try
        {
            return await call();
        }
        catch (SpotifyUnavailableException e)
        {
            throw SpotifyConnections.Unavailable("Spotify did not respond. Try again shortly.", e.RetryAfter);
        }
        catch (Exception e) when (e is SpotifyRequestException or SpotifyAuthorizationException)
        {
            // Spotify refused the app itself: a wrong client secret, or (403) the app owner's Premium has lapsed.
            LogAppRejected(loggers.CreateLogger(typeof(ArtistEndpoints)), e.Message);
            throw SpotifyConnections.Unavailable("Spotify is not available right now. Try again later.");
        }
    }

    [GeneratedRegex("^[0-9A-Za-z]{22}$")]
    private static partial Regex SpotifyIdPattern();

    [LoggerMessage(Level = LogLevel.Error, Message = "Spotify refused an app-token request: {Reason}. Check Spotify:ClientId and Spotify:ClientSecret, and that the app owner has Spotify Premium.")]
    private static partial void LogAppRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not count Spotify releases for artist {SpotifyArtistId}: {Reason}")]
    private static partial void LogReleaseCountFailed(ILogger logger, string spotifyArtistId, string reason);

    private sealed record TrackedRow(string SpotifyArtistId, Guid Id, string Status);

    private sealed record TrackResult(Guid ArtistId, bool Created);

    private sealed record ArtistRow(
        Guid Id, string Name, string SpotifyArtistId, string? ImageUrl, string? WebsiteUrl, string? MerchUrl, string Status,
        bool AddedByMe, DateTime TrackedAt, DateTime? ActivatedAt);
}
