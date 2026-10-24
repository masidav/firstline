using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Moderation;

/// <summary>
/// Account suspensions (migration 0018). Staff flag an account, for example as a bot, for a number of days. While it
/// lasts the fan can't register for allocations (<c>registration_blocker()</c>) or post: uploads, submissions,
/// sharing, and album logs refuse with <c>403 account_suspended</c> (<see cref="EnsureNotSuspendedAsync"/>). The fan
/// sees it on <c>GET /me</c> and can ask once to be unbanned; staff lift it or deny the request with a reply.
/// </summary>
public static class Suspensions
{
    public static readonly IReadOnlyList<string> Reasons = ["bot", "other"];
    public const int DefaultDays = 30;
    public const int MaxDays = 365;

    public static void MapSuspensions(this RouteGroupBuilder v1)
    {
        v1.MapPost("/me/suspension/appeal", AppealAsync);
        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapGet("/suspensions", ListActiveAsync);
        admin.MapPost("/users/{handle}/suspension", SuspendAsync);
        admin.MapPost("/users/{handle}/suspension/lift", LiftAsync);
        admin.MapPost("/appeals/{appealId:guid}/decision", DecideAppealAsync);
    }

    /// <summary>openapi Appeal: the fan's request to be unbanned and what staff decided.</summary>
    public sealed record Appeal(Guid Id, string Status, string Message, DateTime SubmittedAt, string? Reply, DateTime? DecidedAt);

    /// <summary>openapi Suspension, as the fan sees it on <c>GET /me</c>.</summary>
    public sealed record Suspension(Guid Id, string Reason, string? Message, DateTime StartedAt, DateTime EndsAt, Appeal? Appeal);

    /// <summary>openapi AdminSuspension: the same with the account and the staff note.</summary>
    public sealed record AdminSuspension(
        Guid Id, SubmissionEndpoints.UserRef User, string Reason, string? Message, string? Note, DateTime StartedAt, DateTime EndsAt,
        Appeal? Appeal);

    public sealed record AdminSuspensionPage(IReadOnlyList<AdminSuspension> Items, string? NextCursor);

    /// <summary>The fan's suspension in force, if any (for <c>GET /me</c>).</summary>
    public static async Task<Suspension?> LoadForFanAsync(NpgsqlConnection connection, Guid userId, NpgsqlTransaction? transaction = null)
    {
        var row = await connection.QuerySingleOrDefaultAsync<SuspensionRow>(
            $"{SuspensionSelect} WHERE s.id = (active_suspension(@userId)).id", new { userId }, transaction);
        return row is null ? null : new Suspension(row.Id, row.Reason, row.Message, row.StartedAt, row.EndsAt, AppealOf(row));
    }

    /// <summary>Refuses with <c>403 account_suspended</c> while the fan is suspended.</summary>
    public static async Task EnsureNotSuspendedAsync(NpgsqlConnection connection, Guid userId, NpgsqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var endsAt = await connection.ExecuteScalarAsync<DateTime?>(
            "SELECT (active_suspension(@userId)).ends_at", new { userId }, transaction);
        if (endsAt is { } until)
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "account_suspended", "Account paused",
                $"Staff paused your account until {until.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}, so you can't post or "
                + "register for tickets until then. You can ask to be unbanned from the home screen.");
        }
    }

    private static async Task<IResult> AppealAsync(ClaimsPrincipal user, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var message = Text(body, "message")?.Trim();
        if (string.IsNullOrEmpty(message) || message.Length > 1000)
        {
            throw ApiProblemException.Validation(new FieldError("message", "Tell staff why, in up to 1000 characters."));
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var suspension = await LoadForFanAsync(connection, userId)
                         ?? throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not suspended",
                             "Your account isn't paused.");
        if (suspension.Appeal is not null)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already asked",
                "You've already asked to be unbanned for this. Staff will decide, or have decided.");
        }

        await connection.ExecuteAsync(
            "INSERT INTO suspension_appeals (suspension_id, message) VALUES (@id, @message) ON CONFLICT (suspension_id) DO NOTHING",
            new { suspension.Id, message });
        return Results.Created((string?)null, await LoadForFanAsync(connection, userId));
    }

    private static async Task<IResult> ListActiveAsync(int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw ApiProblemException.Validation(new FieldError("limit", "Use a limit from 1 to 100."));
        }

        var offset = 0;
        if (cursor is not null && !PageCursor.TryRead(cursor, out offset))
        {
            throw ApiProblemException.Validation(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        var pageSize = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        // Waiting requests to be unbanned first (oldest first), then the rest by when they end.
        var rows = (await connection.QueryAsync<SuspensionRow>(
            $"""
             {SuspensionSelect}
             WHERE s.lifted_at IS NULL AND s.ends_at > now()
             ORDER BY (ap.id IS NOT NULL AND ap.decided_at IS NULL) DESC, ap.created_at, s.ends_at, s.id
             LIMIT @take OFFSET @offset
             """,
            new { take = pageSize + 1, offset })).ToList();
        return Results.Ok(new AdminSuspensionPage(
            [.. rows.Take(pageSize).Select(ToAdmin)], rows.Count > pageSize ? PageCursor.Write(offset + pageSize) : null));
    }

    private static async Task<IResult> SuspendAsync(
        ClaimsPrincipal user, string handle, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var reason = Text(body, "reason");
        if (reason is null || !Reasons.Contains(reason))
        {
            errors.Add(new FieldError("reason", "Use bot or other."));
        }

        var days = DefaultDays;
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("days", out var d) && d.ValueKind != JsonValueKind.Null
            && !(d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out days) && days is >= 1 and <= MaxDays))
        {
            errors.Add(new FieldError("days", $"Use 1 to {MaxDays} days."));
        }

        var message = Text(body, "message")?.Trim();
        if (message is { Length: > 500 })
        {
            errors.Add(new FieldError("message", "Use up to 500 characters."));
        }

        var note = Text(body, "note")?.Trim();
        if (note is { Length: > 2000 })
        {
            errors.Add(new FieldError("note", "Use up to 2000 characters."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var staffId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var target = await FindUserAsync(connection, handle, transaction);
        if (target == staffId)
        {
            throw ApiProblemException.Validation(new FieldError("handle", "You can't pause your own account."));
        }

        await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('suspension:' || @target::text, 0))", new { target }, transaction);
        if (await connection.ExecuteScalarAsync<bool>("SELECT is_suspended(@target)", new { target }, transaction))
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already paused",
                "This account is already paused. Lift it first to change it.");
        }

        var id = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO account_suspensions (user_id, reason, message, note, created_by, ends_at)
            VALUES (@target, @reason, @message, @note, @staffId, now() + make_interval(days => @days)) RETURNING id
            """,
            new { target, reason, message = Empty(message), note = Empty(note), staffId, days }, transaction);
        await AuditAsync(connection, transaction, staffId, target, "suspend", note);
        var row = await connection.QuerySingleAsync<SuspensionRow>($"{SuspensionSelect} WHERE s.id = @id", new { id }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Created((string?)null, ToAdmin(row));
    }

    private static async Task<IResult> LiftAsync(
        ClaimsPrincipal user, string handle, JsonElement? body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var note = body is { } b ? Text(b, "note")?.Trim() : null;
        if (note is { Length: > 2000 })
        {
            throw ApiProblemException.Validation(new FieldError("note", "Use up to 2000 characters."));
        }

        var staffId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var target = await FindUserAsync(connection, handle, transaction);
        if (await connection.ExecuteAsync(
                "UPDATE account_suspensions SET lifted_at = now(), lifted_by = @staffId WHERE user_id = @target AND lifted_at IS NULL AND ends_at > now()",
                new { target, staffId }, transaction) == 0)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not paused", "This account isn't paused.");
        }

        await AuditAsync(connection, transaction, staffId, target, "lift_suspension", note);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DecideAppealAsync(
        ClaimsPrincipal user, Guid appealId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var decision = Text(body, "decision");
        var reply = Text(body, "reply")?.Trim();
        var errors = new List<FieldError>();
        if (decision is not ("lift" or "deny"))
        {
            errors.Add(new FieldError("decision", "Use lift or deny."));
        }

        if (decision == "deny" && string.IsNullOrEmpty(reply))
        {
            errors.Add(new FieldError("reply", "Tell the fan why (they see this)."));
        }

        if (reply is { Length: > 500 })
        {
            errors.Add(new FieldError("reply", "Use up to 500 characters."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var staffId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var appeal = await connection.QuerySingleOrDefaultAsync<(Guid SuspensionId, Guid UserId, bool Decided)>(
            """
            SELECT ap.suspension_id, s.user_id, ap.decided_at IS NOT NULL
            FROM suspension_appeals ap JOIN account_suspensions s ON s.id = ap.suspension_id
            WHERE ap.id = @appealId FOR UPDATE OF ap
            """,
            new { appealId }, transaction);
        if (appeal.SuspensionId == Guid.Empty)
        {
            throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Request not found", "No request has this id.");
        }

        if (appeal.Decided)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already decided", "Staff already decided this request.");
        }

        var lift = decision == "lift";
        await connection.ExecuteAsync(
            """
            UPDATE suspension_appeals SET decided_at = now(), decision = @outcome, reply = @reply, decided_by = @staffId WHERE id = @appealId;
            UPDATE account_suspensions SET lifted_at = now(), lifted_by = @staffId
             WHERE @lift AND id = @suspensionId AND lifted_at IS NULL;
            """,
            new { appealId, outcome = lift ? "lifted" : "denied", reply = Empty(reply), staffId, lift, appeal.SuspensionId }, transaction);
        await AuditAsync(connection, transaction, staffId, appeal.UserId, lift ? "lift_suspension" : "deny_appeal", reply);
        var row = await connection.QuerySingleAsync<SuspensionRow>(
            $"{SuspensionSelect} WHERE s.id = @suspensionId", new { appeal.SuspensionId }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToAdmin(row));
    }

    private static async Task<Guid> FindUserAsync(NpgsqlConnection connection, string handle, NpgsqlTransaction transaction) =>
        await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM users WHERE handle = @handle AND deleted_at IS NULL", new { handle }, transaction)
        ?? throw new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "User not found", "No account has this handle.");

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid staffId, Guid target, string action, string? note) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO moderation_actions (staff_id, target_type, target_id, action, note, reports)
            VALUES (@staffId, 'account', @target, @action, @note, 0)
            """,
            new { staffId, target, action, note = Empty(note) }, transaction);

    private const string SuspensionSelect =
        """
        SELECT s.id AS Id, s.reason AS Reason, s.message AS Message, s.note AS Note, s.created_at AS StartedAt, s.ends_at AS EndsAt,
               u.handle::text AS Handle, u.display_name AS DisplayName, u.deleted_at IS NOT NULL AS Deleted,
               ap.id AS AppealId, ap.message AS AppealMessage, ap.created_at AS AppealSubmittedAt, ap.decision AS AppealDecision,
               ap.reply AS AppealReply, ap.decided_at AS AppealDecidedAt
        FROM account_suspensions s JOIN users u ON u.id = s.user_id
        LEFT JOIN suspension_appeals ap ON ap.suspension_id = s.id
        """;

    private static Appeal? AppealOf(SuspensionRow row) =>
        row.AppealId is { } id
            ? new Appeal(id, row.AppealDecision ?? "pending", row.AppealMessage!, row.AppealSubmittedAt!.Value, row.AppealReply, row.AppealDecidedAt)
            : null;

    private static AdminSuspension ToAdmin(SuspensionRow row) =>
        new(row.Id, new SubmissionEndpoints.UserRef(row.Deleted ? null : row.Handle, row.Deleted, row.Deleted ? null : row.DisplayName, null),
            row.Reason, row.Message, row.Note, row.StartedAt, row.EndsAt, AppealOf(row));

    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private sealed record SuspensionRow(
        Guid Id, string Reason, string? Message, string? Note, DateTime StartedAt, DateTime EndsAt, string? Handle, string? DisplayName,
        bool Deleted, Guid? AppealId, string? AppealMessage, DateTime? AppealSubmittedAt, string? AppealDecision, string? AppealReply,
        DateTime? AppealDecidedAt);
}
