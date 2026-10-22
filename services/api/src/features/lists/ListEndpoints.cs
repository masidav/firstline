using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.AlbumJournal;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Lists;

public record AlbumList(Guid Id, UserRef User, string Title, string? Description, string Visibility, int ItemCount, DateTime CreatedAt);

public sealed record ListItem(AlbumRef Album, int Position, string? Note);

public sealed record AlbumListDetail(
    Guid Id, UserRef User, string Title, string? Description, string Visibility, int ItemCount, DateTime CreatedAt,
    IReadOnlyList<ListItem> Items)
    : AlbumList(Id, User, Title, Description, Visibility, ItemCount, CreatedAt);

public sealed record ListPage(IReadOnlyList<AlbumList> Items, string? NextCursor);

/// <summary>
/// openapi tag Lists (and <c>GET /users/{handle}/lists</c>): ordered album lists. Positions are 1-based and
/// contiguous; inserting or moving shifts the neighbours (the unique position constraint is deferred, so a
/// whole reorder commits at once). An album appears at most once per list (<c>409 conflict</c>). Visibility
/// follows the log rules: public to everyone, private is 404 to others, followers-only equals private until
/// follows exist (Phase 8).
/// </summary>
public static class ListEndpoints
{
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 2000;
    public const int NoteMaxLength = 500;

    public static void MapLists(this RouteGroupBuilder v1)
    {
        v1.MapPost("/lists", CreateAsync);
        v1.MapGet("/me/lists", ListMineAsync);
        v1.MapGet("/lists/{listId:guid}", GetAsync);
        v1.MapPatch("/lists/{listId:guid}", UpdateAsync);
        v1.MapDelete("/lists/{listId:guid}", DeleteAsync);
        v1.MapPost("/lists/{listId:guid}/items", AddItemAsync);
        v1.MapPatch("/lists/{listId:guid}/items/{albumId:guid}", UpdateItemAsync);
        v1.MapDelete("/lists/{listId:guid}/items/{albumId:guid}", RemoveItemAsync);
        v1.MapGet("/users/{handle}/lists", ListForUserAsync);
    }

    private const string VisibleToViewer = "(li.user_id = @viewer OR li.visibility = 'public')";

    private const string Select =
        """
        SELECT li.id AS Id, li.title AS Title, li.description AS Description, li.visibility::text AS Visibility,
               li.created_at AS CreatedAt, (SELECT count(*) FROM list_items i WHERE i.list_id = li.id)::int AS ItemCount,
               li.user_id AS UserId, u.handle::text AS UserHandle, (u.deleted_at IS NOT NULL) AS UserDeleted,
               u.display_name AS UserDisplayName, u.avatar_url AS UserAvatarUrl
        FROM lists li JOIN users u ON u.id = li.user_id
        """;

    private sealed record Row(
        Guid Id, string Title, string? Description, string Visibility, DateTime CreatedAt, int ItemCount, Guid UserId,
        string? UserHandle, bool UserDeleted, string? UserDisplayName, string? UserAvatarUrl);

    private sealed record ItemRow(
        int Position, string? Note, Guid AlbumId, string AlbumTitle, string? AlbumCoverUrl, string? AlbumSpotifyAlbumId,
        string AlbumType, Guid ArtistId, string ArtistName, string? ArtistImageUrl);

    private sealed class ListFields
    {
        public string? Title { get; set; }
        public bool DescriptionSent { get; set; }
        public string? Description { get; set; }
        public string? Visibility { get; set; }
    }

    private static AlbumList ToList(Row r) =>
        new(r.Id, new UserRef(r.UserDeleted ? null : r.UserHandle, r.UserDeleted, r.UserDisplayName, r.UserAvatarUrl),
            r.Title, r.Description, r.Visibility, r.ItemCount, r.CreatedAt);

    private static async Task<IResult> CreateAsync(ClaimsPrincipal user, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var fields = Parse(body, creating: true);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var listId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO lists (user_id, title, description, visibility) VALUES (@userId, @title, @description, @visibility::visibility) RETURNING id",
            new { userId = user.UserId(), fields.Title, fields.Description, visibility = fields.Visibility ?? "public" });
        return Results.Created($"/v1/lists/{listId}", await LoadAsync(connection, null, listId));
    }

    private static async Task<IResult> ListMineAsync(
        ClaimsPrincipal user, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        return await PageAsync(connection, "li.user_id = @owner", new { owner = user.UserId(), viewer = user.UserId() }, limit, cursor);
    }

    private static async Task<IResult> ListForUserAsync(
        ClaimsPrincipal user, string handle, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var ownerId = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM users WHERE handle = @handle AND deleted_at IS NULL", new { handle })
            ?? throw Journal.NotFound("User");
        return await PageAsync(connection, $"li.user_id = @owner AND {VisibleToViewer}", new { owner = ownerId, viewer = user.UserId() }, limit, cursor);
    }

    private static async Task<IResult> PageAsync(NpgsqlConnection connection, string where, object filter, int? limit, string? cursor)
    {
        var errors = new List<FieldError>();
        var (take, offset) = Journal.ReadPage(limit, cursor, errors);
        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var parameters = new DynamicParameters(filter);
        parameters.Add("take", take + 1);
        parameters.Add("offset", offset);
        var rows = (await connection.QueryAsync<Row>(
            $"{Select} WHERE {where} ORDER BY li.created_at DESC, li.id LIMIT @take OFFSET @offset", parameters)).ToList();
        var (items, next) = Journal.Page(rows, take, offset);
        return Results.Ok(new ListPage([.. items.Select(ToList)], next));
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, Guid listId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            $"{Select} WHERE li.id = @listId AND {VisibleToViewer}", new { listId, viewer = user.UserId() })
            ?? throw Journal.NotFound("List");
        return Results.Ok(await DetailAsync(connection, null, row));
    }

    private static async Task<IResult> UpdateAsync(
        ClaimsPrincipal user, Guid listId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var fields = Parse(body, creating: false);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireOwnAsync(connection, transaction, listId, user.UserId(), "Only the list's owner can edit it.");

        var sets = new List<string>();
        var parameters = new DynamicParameters(new { listId });
        if (fields.Title is { } title)
        {
            sets.Add("title = @title");
            parameters.Add("title", title);
        }

        if (fields.DescriptionSent)
        {
            sets.Add("description = @description");
            parameters.Add("description", fields.Description);
        }

        if (fields.Visibility is { } visibility)
        {
            sets.Add("visibility = @visibility::visibility");
            parameters.Add("visibility", visibility);
        }

        if (sets.Count > 0)
        {
            await connection.ExecuteAsync($"UPDATE lists SET {string.Join(", ", sets)} WHERE id = @listId", parameters, transaction);
        }

        var list = await LoadAsync(connection, transaction, listId);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(list);
    }

    private static async Task<IResult> DeleteAsync(ClaimsPrincipal user, Guid listId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireOwnAsync(connection, transaction, listId, user.UserId(), "Only the list's owner can delete it.");
        await connection.ExecuteAsync("DELETE FROM lists WHERE id = @listId", new { listId }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    // Appends, or inserts at `position` and shifts later items down.
    private static async Task<IResult> AddItemAsync(
        ClaimsPrincipal user, Guid listId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var (albumId, note, position) = ParseItem(body, creating: true);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireOwnAsync(connection, transaction, listId, user.UserId(), "Only the list's owner can add to it.");
        if (!await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM albums WHERE id = @albumId AND delisted_at IS NULL)", new { albumId }, transaction))
        {
            throw Journal.NotFound("Album");
        }

        if (await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM list_items WHERE list_id = @listId AND album_id = @albumId)", new { listId, albumId }, transaction))
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Album already on the list",
                "This album is already on the list.");
        }

        var count = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM list_items WHERE list_id = @listId", new { listId }, transaction);
        var at = Math.Clamp(position ?? count + 1, 1, count + 1);
        await connection.ExecuteAsync(
            """
            UPDATE list_items SET position = position + 1 WHERE list_id = @listId AND position >= @at;
            INSERT INTO list_items (list_id, album_id, position, note) VALUES (@listId, @albumId, @at, @note);
            """,
            new { listId, albumId, at, note }, transaction);
        var detail = await DetailAsync(connection, transaction, listId);
        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/v1/lists/{listId}", detail);
    }

    // Changes the note and/or moves the item; the items in between shift to keep positions contiguous.
    private static async Task<IResult> UpdateItemAsync(
        ClaimsPrincipal user, Guid listId, Guid albumId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var (_, note, position) = ParseItem(body, creating: false);
        var noteSent = body.TryGetProperty("note", out _);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireOwnAsync(connection, transaction, listId, user.UserId(), "Only the list's owner can change it.");
        var current = await connection.ExecuteScalarAsync<int?>(
            "SELECT position FROM list_items WHERE list_id = @listId AND album_id = @albumId", new { listId, albumId }, transaction)
            ?? throw Journal.NotFound("List item");

        if (noteSent)
        {
            await connection.ExecuteAsync("UPDATE list_items SET note = @note WHERE list_id = @listId AND album_id = @albumId",
                new { note, listId, albumId }, transaction);
        }

        if (position is { } wanted)
        {
            var count = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM list_items WHERE list_id = @listId", new { listId }, transaction);
            var target = Math.Clamp(wanted, 1, count);
            if (target != current)
            {
                await connection.ExecuteAsync(
                    """
                    UPDATE list_items SET position = CASE
                        WHEN album_id = @albumId THEN @target
                        WHEN @target > @current AND position > @current AND position <= @target THEN position - 1
                        WHEN @target < @current AND position >= @target AND position < @current THEN position + 1
                        ELSE position END
                    WHERE list_id = @listId
                    """,
                    new { listId, albumId, target, current }, transaction);
            }
        }

        var detail = await DetailAsync(connection, transaction, listId);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(detail);
    }

    private static async Task<IResult> RemoveItemAsync(
        ClaimsPrincipal user, Guid listId, Guid albumId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequireOwnAsync(connection, transaction, listId, user.UserId(), "Only the list's owner can change it.");
        var removed = await connection.ExecuteScalarAsync<int?>(
            "DELETE FROM list_items WHERE list_id = @listId AND album_id = @albumId RETURNING position",
            new { listId, albumId }, transaction)
            ?? throw Journal.NotFound("List item");
        await connection.ExecuteAsync("UPDATE list_items SET position = position - 1 WHERE list_id = @listId AND position > @removed",
            new { listId, removed }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<AlbumList> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid listId) =>
        ToList(await connection.QuerySingleAsync<Row>($"{Select} WHERE li.id = @listId", new { listId }, transaction));

    private static async Task<AlbumListDetail> DetailAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid listId) =>
        await DetailAsync(connection, transaction, await connection.QuerySingleAsync<Row>($"{Select} WHERE li.id = @listId", new { listId }, transaction));

    private static async Task<AlbumListDetail> DetailAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Row row)
    {
        var items = await connection.QueryAsync<ItemRow>(
            """
            SELECT i.position AS Position, i.note AS Note, a.id AS AlbumId, a.title AS AlbumTitle,
                   CASE WHEN a.delisted_at IS NULL THEN a.cover_url END AS AlbumCoverUrl,
                   CASE WHEN a.delisted_at IS NULL THEN a.spotify_album_id END AS AlbumSpotifyAlbumId, a.album_type AS AlbumType,
                   ar.id AS ArtistId, ar.name AS ArtistName, ar.image_url AS ArtistImageUrl
            FROM list_items i JOIN albums a ON a.id = i.album_id JOIN artists ar ON ar.id = a.artist_id
            WHERE i.list_id = @listId ORDER BY i.position
            """,
            new { listId = row.Id }, transaction);
        var list = ToList(row);
        return new AlbumListDetail(list.Id, list.User, list.Title, list.Description, list.Visibility, list.ItemCount, list.CreatedAt,
            [.. items.Select(i => new ListItem(
                new AlbumRef(i.AlbumId, i.AlbumTitle, i.AlbumCoverUrl, new ArtistRef(i.ArtistId, i.ArtistName, i.ArtistImageUrl),
                    i.AlbumSpotifyAlbumId, i.AlbumType),
                i.Position, i.Note))]);
    }

    // 404 when the list is gone or not visible to the caller, 403 when visible but someone else's. Locks the row.
    private static async Task RequireOwnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid listId, Guid userId, string forbiddenDetail)
    {
        var owner = await connection.QuerySingleOrDefaultAsync<(Guid UserId, string Visibility)?>(
            "SELECT user_id, visibility::text FROM lists WHERE id = @listId FOR UPDATE", new { listId }, transaction);
        if (owner is null || (owner.Value.UserId != userId && owner.Value.Visibility != "public"))
        {
            throw Journal.NotFound("List");
        }

        if (owner.Value.UserId != userId)
        {
            throw Journal.Forbidden(forbiddenDetail);
        }
    }

    private static ListFields Parse(JsonElement body, bool creating)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiProblemException.Validation(new FieldError("body", "Send a JSON object."));
        }

        var fields = new ListFields();
        var errors = new List<FieldError>();
        if (body.TryGetProperty("title", out var title) || creating)
        {
            var text = title.ValueKind == JsonValueKind.String ? title.GetString()!.Trim() : "";
            if (text.Length == 0 || text.EnumerateRunes().Count() > TitleMaxLength)
            {
                errors.Add(new FieldError("title", $"Use 1 to {TitleMaxLength} characters."));
            }
            else
            {
                fields.Title = text;
            }
        }

        if (body.TryGetProperty("description", out var description))
        {
            fields.DescriptionSent = true;
            if (description.ValueKind == JsonValueKind.String)
            {
                var text = description.GetString()!.Trim();
                if (text.EnumerateRunes().Count() > DescriptionMaxLength)
                {
                    errors.Add(new FieldError("description", $"Use at most {DescriptionMaxLength} characters."));
                }

                fields.Description = text.Length == 0 ? null : text;
            }
            else if (description.ValueKind != JsonValueKind.Null)
            {
                errors.Add(new FieldError("description", "Send text, or null to clear it."));
            }
        }

        if (body.TryGetProperty("visibility", out var visibility))
        {
            if (visibility.ValueKind == JsonValueKind.String && Journal.Visibilities.Contains(visibility.GetString()!, StringComparer.Ordinal))
            {
                fields.Visibility = visibility.GetString();
            }
            else
            {
                errors.Add(new FieldError("visibility", "Use public, followers, or private."));
            }
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        return fields;
    }

    private static (Guid AlbumId, string? Note, int? Position) ParseItem(JsonElement body, bool creating)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiProblemException.Validation(new FieldError("body", "Send a JSON object."));
        }

        var errors = new List<FieldError>();
        Guid albumId = default;
        if (creating && !(body.TryGetProperty("album_id", out var album) && album.ValueKind == JsonValueKind.String
                          && Guid.TryParse(album.GetString(), out albumId)))
        {
            errors.Add(new FieldError("album_id", "Send the album's id."));
        }

        string? note = null;
        if (body.TryGetProperty("note", out var noteValue))
        {
            if (noteValue.ValueKind == JsonValueKind.String)
            {
                var text = noteValue.GetString()!.Trim();
                if (text.EnumerateRunes().Count() > NoteMaxLength)
                {
                    errors.Add(new FieldError("note", $"Use at most {NoteMaxLength} characters."));
                }

                note = text.Length == 0 ? null : text;
            }
            else if (noteValue.ValueKind != JsonValueKind.Null)
            {
                errors.Add(new FieldError("note", "Send text, or null to clear it."));
            }
        }

        int? position = null;
        if (body.TryGetProperty("position", out var positionValue))
        {
            if (positionValue.ValueKind == JsonValueKind.Number && positionValue.TryGetInt32(out var value) && value >= 1)
            {
                position = value;
            }
            else
            {
                errors.Add(new FieldError("position", "Use a whole number from 1."));
            }
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        return (albumId, note, position);
    }
}
