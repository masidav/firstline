using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Fraud;

/// <summary>
/// Fraud review (migration 0042): staff flag a fan, which holds them out of fan tickets (no registering; the draw and
/// returned seats pass them by) until the flag is cleared. The fan sees the reason category and can appeal once per
/// flag; staff grant (clears the flag) or deny (upholds it). The rules and the eligibility audit log live in the
/// database; this file checks input and shapes the answers. A rule flag stays hidden from the fan until staff uphold
/// it (<c>shown_to_fan_at</c>, migration 0044).
/// </summary>
public static class FraudEndpoints
{
    public static readonly IReadOnlyList<string> Reasons = ["streaming_farm", "bot", "shared_device", "duplicate_account", "other"];
    private static readonly IReadOnlyList<string> QueueFilters = ["needs_action", "open", "upheld", "cleared"];
    private const int MaxText = 2000;

    public static void MapFraud(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/fraud-review", MyReviewAsync);
        v1.MapPost("/me/fraud-review/flags/{flagId:guid}/appeal", AppealAsync);
        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapGet("/fraud-flags", QueueAsync);
        admin.MapPost("/fraud-flags", RaiseAsync);
        admin.MapPost("/fraud-flags/{flagId:guid}/decision", DecideFlagAsync);
        admin.MapPost("/fraud-appeals/{appealId:guid}/decision", DecideAppealAsync);
        admin.MapGet("/eligibility-audit", AuditAsync);
    }

    public sealed record Appeal(Guid Id, Guid FlagId, string Message, DateTime FiledAt, string Status, DateTime? DecidedAt, string? Response);

    public sealed record FanFlag(Guid Id, string Reason, string Status, DateTime RaisedAt, DateTime? DecidedAt, bool CanAppeal, Appeal? Appeal);

    public sealed record FanReview(bool OnHold, IReadOnlyList<FanFlag> Flags);

    /// <summary><c>NextDrawClosesAt</c>: when registration closes for the soonest draw the fan is in, if any.</summary>
    public sealed record FlagContext(
        DateTime AccountCreatedAt, JsonElement TrustParts, int LiveRegistrations, int HeldReservations, DateTime? NextDrawClosesAt);

    public sealed record Flag(
        Guid Id, Guid UserId, SubmissionEndpoints.UserRef User, string Reason, JsonElement Evidence, string? Note, string Source,
        string? RaisedBy, DateTime RaisedAt, string Status, string? DecidedBy, DateTime? DecidedAt, string? DecisionNote,
        Appeal? Appeal, string? AppealDecidedBy, FlagContext Context, DateTime? ShownToFanAt);

    public sealed record FlagPage(IReadOnlyList<Flag> Items, string? NextCursor);

    public sealed record AuditEntry(
        long Id, DateTime At, string Action, string? Reason, Guid UserId, string? Handle, Guid? AllocationId, Guid? RegistrationId,
        Guid? FlagId, Guid? AppealId, string? Actor, JsonElement Inputs);

    public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, string? NextCursor);

    // ------------------------------------------------------------------ fans

    private static async Task<IResult> MyReviewAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FanFlagRow>(
            $"""
             SELECT f.id AS Id, f.reason AS Reason, f.status AS Status, f.raised_at AS RaisedAt, f.decided_at AS DecidedAt,
                    {AppealColumns}
               FROM fraud_flags f LEFT JOIN fraud_appeals ap ON ap.flag_id = f.id
              WHERE f.user_id = @userId AND f.shown_to_fan_at IS NOT NULL
              ORDER BY f.raised_at DESC
             """,
            new { userId });
        var flags = rows.Select(r => new FanFlag(r.Id, r.Reason, r.Status, r.RaisedAt, r.DecidedAt,
            r.Status != "cleared" && r.AppealId is null, r.ToAppeal())).ToList();
        return Results.Ok(new FanReview(flags.Any(f => f.Status != "cleared"), flags));
    }

    private static async Task<IResult> AppealAsync(
        ClaimsPrincipal user, Guid flagId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var message = Text(body, "message")?.Trim();
        if (message is not { Length: > 0 and <= MaxText })
        {
            throw ApiProblemException.Validation(new FieldError("message", $"Write your appeal in 1 to {MaxText} characters."));
        }

        var userId = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM fraud_flags WHERE id = @flagId AND user_id = @userId AND shown_to_fan_at IS NOT NULL)",
                new { flagId, userId }, transaction))
        {
            throw NotFound("Review not found.");
        }

        Guid appealId;
        try
        {
            appealId = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO fraud_appeals (flag_id, user_id, message) VALUES (@flagId, @userId, @message) RETURNING id",
                new { flagId, userId, message }, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw AppealUnavailable("You've already appealed this review.");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException && e.MessageText == "appeal blocked: not_shown")
        {
            throw NotFound("Review not found.");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException && e.MessageText == "appeal blocked: flag_cleared")
        {
            throw AppealUnavailable("This review is already cleared.");
        }

        var appeal = await LoadAppealAsync(connection, appealId);
        return Results.Created($"/v1/me/fraud-review", appeal);
    }

    // ------------------------------------------------------------------ staff

    private static async Task<IResult> QueueAsync(
        string? status, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        status ??= "needs_action";
        if (!QueueFilters.Contains(status))
        {
            throw ApiProblemException.Validation(new FieldError("status", "Use needs_action, open, upheld, or cleared."));
        }

        var (pageSize, offset) = Page(limit, cursor);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var ids = (await connection.QueryAsync<Guid>(
            status == "needs_action"
                ? """
                  SELECT f.id FROM fraud_flags f LEFT JOIN fraud_appeals ap ON ap.flag_id = f.id
                   WHERE f.status = 'open' OR ap.status = 'pending'
                   ORDER BY (SELECT min(a.registration_closes_at) FROM allocation_registrations r JOIN allocations a ON a.id = r.allocation_id
                              WHERE r.user_id = f.user_id AND r.withdrawn_at IS NULL AND r.outcome IS NULL) NULLS LAST,
                            COALESCE(LEAST(f.raised_at, ap.filed_at), f.raised_at), f.id
                   LIMIT @take OFFSET @offset
                  """
                : "SELECT id FROM fraud_flags WHERE status = @status ORDER BY raised_at DESC, id LIMIT @take OFFSET @offset",
            new { status, take = pageSize + 1, offset })).ToList();

        var items = new List<Flag>();
        foreach (var id in ids.Take(pageSize))
        {
            items.Add(await LoadFlagAsync(connection, id));
        }

        return Results.Ok(new FlagPage(items, ids.Count > pageSize ? PageCursor.Write(offset + pageSize) : null));
    }

    private static async Task<IResult> RaiseAsync(
        ClaimsPrincipal user, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var handle = Text(body, "handle")?.Trim().TrimStart('@');
        if (handle is not { Length: > 0 })
        {
            errors.Add(new FieldError("handle", "Send the fan's handle."));
        }

        var reason = Text(body, "reason");
        if (reason is null || !Reasons.Contains(reason))
        {
            errors.Add(new FieldError("reason", "Use streaming_farm, bot, shared_device, duplicate_account, or other."));
        }

        var note = Note(body, "note", errors);
        var evidence = "{}";
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("evidence", out var e) && e.ValueKind != JsonValueKind.Null)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new FieldError("evidence", "Send an object."));
            }
            else
            {
                evidence = e.GetRawText();
            }
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var fanId = await connection.ExecuteScalarAsync<Guid?>(
                        "SELECT id FROM users WHERE lower(handle) = lower(@handle) AND deleted_at IS NULL", new { handle })
                    ?? throw NotFound("No account has this handle.");
        Guid flagId;
        try
        {
            flagId = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO fraud_flags (user_id, reason, evidence, note, source, raised_by)
                VALUES (@fanId, @reason, @evidence::jsonb, @note, 'staff', @by) RETURNING id
                """,
                new { fanId, reason, evidence, note, by = user.UserId() });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "flag_open", "Already under review",
                "This fan already has an open flag. Decide it, or add to its review.");
        }

        return Results.Created($"/v1/admin/fraud-flags/{flagId}", await LoadFlagAsync(connection, flagId));
    }

    private static async Task<IResult> DecideFlagAsync(
        ClaimsPrincipal user, Guid flagId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var decision = Text(body, "decision");
        if (decision is not ("uphold" or "clear"))
        {
            errors.Add(new FieldError("decision", "Use uphold or clear."));
        }

        var note = Note(body, "note", errors);
        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.ExecuteScalarAsync<string?>(
                          "SELECT status FROM fraud_flags WHERE id = @flagId FOR UPDATE", new { flagId }, transaction)
                      ?? throw NotFound("Flag not found.");
        var target = decision == "uphold" ? "upheld" : "cleared";
        if (current == "cleared" || current == target)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already decided",
                current == "cleared" ? "This flag is cleared, which is final." : "This flag is already upheld.");
        }

        await connection.ExecuteAsync(
            "UPDATE fraud_flags SET status = @target, decided_by = @by, decision_note = @note WHERE id = @flagId",
            new { flagId, target, by = user.UserId(), note }, transaction);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(await LoadFlagAsync(connection, flagId));
    }

    private static async Task<IResult> DecideAppealAsync(
        ClaimsPrincipal user, Guid appealId, JsonElement body, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        var decision = Text(body, "decision");
        if (decision is not ("grant" or "deny"))
        {
            errors.Add(new FieldError("decision", "Use grant or deny."));
        }

        var response = Note(body, "response", errors);
        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid flagId;
        try
        {
            flagId = await connection.ExecuteScalarAsync<Guid>(
                "SELECT flag_id FROM decide_fraud_appeal(@appealId, @by, @grant, @response)",
                new { appealId, by = user.UserId(), grant = decision == "grant", response }, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException && e.MessageText.StartsWith("appeal blocked: ", StringComparison.Ordinal))
        {
            throw e.MessageText == "appeal blocked: not_found"
                ? NotFound("Appeal not found.")
                : AppealUnavailable("This appeal is already decided.");
        }

        return Results.Ok(await LoadFlagAsync(connection, flagId));
    }

    private static async Task<IResult> AuditAsync(
        string? handle, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "allocation_id")] Guid? allocationId, int? limit, string? cursor,
        NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        handle = handle?.Trim().TrimStart('@');
        if (string.IsNullOrEmpty(handle) && allocationId is null)
        {
            throw ApiProblemException.Validation(new FieldError("handle", "Send a handle, an allocation_id, or both."));
        }

        var (pageSize, offset) = Page(limit, cursor);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        Guid? userId = null;
        if (!string.IsNullOrEmpty(handle))
        {
            userId = await connection.ExecuteScalarAsync<Guid?>("SELECT id FROM users WHERE lower(handle) = lower(@handle)", new { handle })
                     ?? throw NotFound("No account has this handle.");
        }

        var rows = (await connection.QueryAsync<AuditRow>(
            """
            SELECT x.id AS Id, x.at AS At, x.action AS Action, x.reason AS Reason, x.user_id AS UserId,
                   CASE WHEN u.deleted_at IS NULL THEN u.handle::text END AS Handle, x.allocation_id AS AllocationId,
                   x.registration_id AS RegistrationId, x.flag_id AS FlagId, x.appeal_id AS AppealId,
                   a.handle::text AS Actor, x.inputs::text AS Inputs
              FROM eligibility_audit x JOIN users u ON u.id = x.user_id LEFT JOIN users a ON a.id = x.actor_id
             WHERE (@userId::uuid IS NULL OR x.user_id = @userId) AND (@allocationId::uuid IS NULL OR x.allocation_id = @allocationId)
             ORDER BY x.id DESC
             LIMIT @take OFFSET @offset
            """,
            new { userId, allocationId, take = pageSize + 1, offset })).ToList();
        var items = rows.Take(pageSize).Select(r => new AuditEntry(r.Id, r.At, r.Action, r.Reason, r.UserId, r.Handle, r.AllocationId,
            r.RegistrationId, r.FlagId, r.AppealId, r.Actor, Parse(r.Inputs))).ToList();
        return Results.Ok(new AuditPage(items, rows.Count > pageSize ? PageCursor.Write(offset + pageSize) : null));
    }

    // ------------------------------------------------------------------ loading

    private const string AppealColumns =
        """
        ap.id AS AppealId, ap.message AS AppealMessage, ap.filed_at AS AppealFiledAt, ap.status AS AppealStatus,
        ap.decided_at AS AppealDecidedAt, ap.response AS AppealResponse
        """;

    private static async Task<Appeal> LoadAppealAsync(NpgsqlConnection connection, Guid appealId) =>
        await connection.QuerySingleAsync<Appeal>(
            """
            SELECT id AS Id, flag_id AS FlagId, message AS Message, filed_at AS FiledAt, status AS Status, decided_at AS DecidedAt,
                   response AS Response
              FROM fraud_appeals WHERE id = @appealId
            """,
            new { appealId });

    private static async Task<Flag> LoadFlagAsync(NpgsqlConnection connection, Guid flagId)
    {
        var r = await connection.QuerySingleAsync<FlagRow>(
            $"""
             SELECT f.id AS Id, f.user_id AS UserId, u.handle::text AS Handle, u.display_name AS DisplayName,
                    u.deleted_at IS NOT NULL AS Deleted, u.created_at AS AccountCreatedAt,
                    f.reason AS Reason, f.evidence::text AS Evidence, f.note AS Note, f.source AS Source,
                    rb.handle::text AS RaisedBy, f.raised_at AS RaisedAt, f.status AS Status, db.handle::text AS DecidedBy,
                    f.decided_at AS DecidedAt, f.decision_note AS DecisionNote,
                    {AppealColumns}, adb.handle::text AS AppealDecidedBy,
                    COALESCE(fan_trust_parts(f.user_id), jsonb_build_object())::text AS TrustParts,
                    (SELECT count(*)::int FROM allocation_registrations x WHERE x.user_id = f.user_id AND x.withdrawn_at IS NULL
                        AND x.outcome IS NULL) AS LiveRegistrations,
                    (SELECT count(*)::int FROM reservations x JOIN events e ON e.id = x.event_id
                      WHERE x.user_id = f.user_id AND x.returned_at IS NULL AND x.cancelled_at IS NULL AND e.starts_at > now()) AS HeldReservations,
                    (SELECT min(a.registration_closes_at) FROM allocation_registrations x JOIN allocations a ON a.id = x.allocation_id
                      WHERE x.user_id = f.user_id AND x.withdrawn_at IS NULL AND x.outcome IS NULL) AS NextDrawClosesAt,
                    f.shown_to_fan_at AS ShownToFanAt
               FROM fraud_flags f JOIN users u ON u.id = f.user_id
               LEFT JOIN users rb ON rb.id = f.raised_by LEFT JOIN users db ON db.id = f.decided_by
               LEFT JOIN fraud_appeals ap ON ap.flag_id = f.id LEFT JOIN users adb ON adb.id = ap.decided_by
              WHERE f.id = @flagId
             """,
            new { flagId });
        return new Flag(r.Id, r.UserId, new SubmissionEndpoints.UserRef(r.Deleted ? null : r.Handle, r.Deleted, r.Deleted ? null : r.DisplayName, null),
            r.Reason, Parse(r.Evidence), r.Note, r.Source, r.RaisedBy, r.RaisedAt, r.Status, r.DecidedBy, r.DecidedAt, r.DecisionNote,
            r.ToAppeal(), r.AppealDecidedBy,
            new FlagContext(r.AccountCreatedAt, Parse(r.TrustParts), r.LiveRegistrations, r.HeldReservations, r.NextDrawClosesAt),
            r.ShownToFanAt);
    }

    private class AppealRow
    {
        public Guid Id { get; init; }
        public Guid? AppealId { get; init; }
        public string? AppealMessage { get; init; }
        public DateTime? AppealFiledAt { get; init; }
        public string? AppealStatus { get; init; }
        public DateTime? AppealDecidedAt { get; init; }
        public string? AppealResponse { get; init; }

        public Appeal? ToAppeal() => AppealId is { } id
            ? new Appeal(id, Id, AppealMessage!, AppealFiledAt!.Value, AppealStatus!, AppealDecidedAt, AppealResponse)
            : null;
    }

    private sealed class FanFlagRow : AppealRow
    {
        public string Reason { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTime RaisedAt { get; init; }
        public DateTime? DecidedAt { get; init; }
    }

    private sealed class FlagRow : AppealRow
    {
        public Guid UserId { get; init; }
        public string? Handle { get; init; }
        public string? DisplayName { get; init; }
        public bool Deleted { get; init; }
        public DateTime AccountCreatedAt { get; init; }
        public string Reason { get; init; } = "";
        public string Evidence { get; init; } = "{}";
        public string? Note { get; init; }
        public string Source { get; init; } = "";
        public string? RaisedBy { get; init; }
        public DateTime RaisedAt { get; init; }
        public string Status { get; init; } = "";
        public string? DecidedBy { get; init; }
        public DateTime? DecidedAt { get; init; }
        public string? DecisionNote { get; init; }
        public string? AppealDecidedBy { get; init; }
        public string TrustParts { get; init; } = "{}";
        public int LiveRegistrations { get; init; }
        public int HeldReservations { get; init; }
        public DateTime? NextDrawClosesAt { get; init; }
        public DateTime? ShownToFanAt { get; init; }
    }

    private sealed record AuditRow(
        long Id, DateTime At, string Action, string? Reason, Guid UserId, string? Handle, Guid? AllocationId, Guid? RegistrationId,
        Guid? FlagId, Guid? AppealId, string? Actor, string Inputs);

    // ------------------------------------------------------------------ helpers

    private static (int PageSize, int Offset) Page(int? limit, string? cursor)
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

        return (limit ?? 25, offset);
    }

    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Note(JsonElement body, string name, List<FieldError> errors)
    {
        var text = Text(body, name)?.Trim();
        if (text is { Length: > MaxText })
        {
            errors.Add(new FieldError(name, $"Use up to {MaxText} characters."));
        }

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ApiProblemException NotFound(string detail) =>
        new(StatusCodes.Status404NotFound, "not_found", "Not found", detail);

    private static ApiProblemException AppealUnavailable(string detail) =>
        new(StatusCodes.Status409Conflict, "appeal_unavailable", "Can't appeal", detail);
}
