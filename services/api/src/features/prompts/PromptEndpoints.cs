using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Prompts;

/// <summary>
/// Music prompts (migrations 0025 and 0034): one open question a week ("What's your best hypothetical band name?"),
/// out on Thursdays (UTC) and open until the next one, from staff or a built-in library. Fans answer once (editable that
/// week); answers show on the feed, public to everyone or followers-only, and can be reported like logs and photos.
/// No points.
/// </summary>
public static class PromptEndpoints
{
    public const int MaxAnswerLength = 280;

    public static void MapPrompts(this RouteGroupBuilder v1)
    {
        v1.MapGet("/prompts/today", GetTodayAsync);
        v1.MapPut("/prompts/{promptId:guid}/answer", AnswerAsync);
        v1.MapDelete("/prompts/{promptId:guid}/answer", WithdrawAsync);
        v1.MapGet("/prompts/{promptId:guid}/answers", ListAnswersAsync);
        v1.MapGet("/users/{handle}/answers", ListForUserAsync);
        var admin = v1.MapGroup("/admin").RequireAuthorization(AuthSetup.StaffPolicy);
        admin.MapGet("/prompts", ListForStaffAsync);
        admin.MapPut("/prompts/{day}", ScheduleAsync);
        admin.MapDelete("/prompts/{day}", UnscheduleAsync);
    }

    public sealed record Prompt(Guid Id, DateOnly Day, string Text, string Source, bool Open, int AnswerCount);

    /// <summary>openapi PromptAnswer. <c>Following</c>: the caller follows its author.</summary>
    public sealed record PromptAnswer(
        Guid Id, Guid PromptId, SubmissionEndpoints.UserRef User, string Body, string Visibility, DateTime CreatedAt, DateTime UpdatedAt,
        bool Mine, bool Following);

    /// <summary><c>FollowingAnswers</c>: up to three answers from people the caller follows, once the caller has answered.</summary>
    /// <summary>
    /// The current prompt: prompts come out on Thursdays (UTC) and stay open until the next one, or every day where
    /// app_settings.engagement_cadence is 'daily' (migration 0051). <c>NextPromptOn</c>: when the next one comes out.
    /// </summary>
    public sealed record PromptToday(Prompt Prompt, PromptAnswer? MyAnswer, IReadOnlyList<PromptAnswer> FollowingAnswers, DateOnly NextPromptOn);

    public sealed record AnswerPage(IReadOnlyList<PromptAnswer> Items, string? NextCursor);

    public sealed record AnswerRequest(string? Body, string? Visibility);

    /// <summary>An answer with its prompt, for a fan's page.</summary>
    public sealed record AnswerWithPrompt(PromptAnswer Answer, string PromptText, DateOnly Day);

    public sealed record AnswersWithPrompts(IReadOnlyList<AnswerWithPrompt> Items);

    public sealed record ScheduleRequest(string? Text);

    public sealed record PromptList(IReadOnlyList<Prompt> Items);

    private static async Task<IResult> GetTodayAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var promptId = await connection.ExecuteScalarAsync<Guid>("SELECT (prompt_for_day(prompt_week_day())).id");
        return Results.Ok(await LoadTodayAsync(connection, me, promptId));
    }

    // Writes or rewrites the caller's answer to this week's prompt.
    private static async Task<IResult> AnswerAsync(
        ClaimsPrincipal user, Guid promptId, AnswerRequest? request, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var body = request?.Body?.Trim();
        var errors = new List<FieldError>();
        if (body is not { Length: > 0 } || body.Length > MaxAnswerLength)
        {
            errors.Add(new FieldError("body", $"Write 1 to {MaxAnswerLength} characters."));
        }

        var visibility = request?.Visibility ?? "public";
        if (visibility is not ("public" or "followers"))
        {
            errors.Add(new FieldError("visibility", "Use public or followers."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await RequireOpenAsync(connection, promptId);
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO prompt_answers (prompt_id, user_id, body, visibility) VALUES (@promptId, @me, @body, @visibility::visibility)
                ON CONFLICT ON CONSTRAINT prompt_answers_one_per_fan DO UPDATE
                  SET body = EXCLUDED.body, visibility = EXCLUDED.visibility, deleted_at = NULL
                  WHERE prompt_answers.removed_at IS NULL
                """,
                new { promptId, me, body, visibility });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException && e.MessageText.Contains("prompt's day", StringComparison.Ordinal))
        {
            throw Closed();   // the day ended in between
        }

        return Results.Ok(await LoadTodayAsync(connection, me, promptId));
    }

    private static async Task<IResult> WithdrawAsync(ClaimsPrincipal user, Guid promptId, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await RequireOpenAsync(connection, promptId);
        await connection.ExecuteAsync(
            "UPDATE prompt_answers SET deleted_at = now() WHERE prompt_id = @promptId AND user_id = @me AND deleted_at IS NULL",
            new { promptId, me = user.UserId() });
        return Results.NoContent();
    }

    // Answers the caller can see: people they follow first, then everyone else's public ones, newest first.
    private static async Task<IResult> ListAnswersAsync(
        ClaimsPrincipal user, Guid promptId, int? limit, string? cursor, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        if (limit is < 1 or > 100)
        {
            errors.Add(new FieldError("limit", "Use a limit from 1 to 100."));
        }

        var offset = 0;
        if (cursor is not null && !PageCursor.TryRead(cursor, out offset))
        {
            errors.Add(new FieldError("cursor", "Send the next_cursor from a previous page."));
        }

        if (errors.Count > 0)
        {
            throw ApiProblemException.Validation([.. errors]);
        }

        var me = user.UserId();
        var take = limit ?? 25;
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM prompts WHERE id = @promptId)", new { promptId }))
        {
            throw NotFound();
        }

        var rows = (await connection.QueryAsync<AnswerRow>(
            $"""
             {AnswerSelect}
             WHERE pa.prompt_id = @promptId AND {Visible}
             ORDER BY (pa.user_id = @me) DESC, Following DESC, pa.created_at DESC, pa.id
             LIMIT @take OFFSET @offset
             """,
            new { promptId, me, take = take + 1, offset })).ToList();
        return Results.Ok(new AnswerPage([.. rows.Take(take).Select(ToAnswer)], rows.Count > take ? PageCursor.Write(offset + take) : null));
    }

    // A fan's last 20 answers the caller may see, newest first.
    private static async Task<IResult> ListForUserAsync(ClaimsPrincipal user, string handle, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var owner = await Follows.FollowEndpoints.LiveUserAsync(connection, handle);
        var rows = await connection.QueryAsync<AnswerRow>(
            $"""
             {AnswerSelect}
             WHERE pa.user_id = @owner AND {Visible} AND (pa.removed_at IS NULL)
             ORDER BY pa.created_at DESC, pa.id LIMIT 20
             """,
            new { owner, me });
        var prompts = (await connection.QueryAsync<(Guid Id, string Text, DateOnly Day)>(
                "SELECT id, text, day FROM prompts WHERE id = ANY(@ids)", new { ids = rows.Select(r => r.PromptId).Distinct().ToArray() }))
            .ToDictionary(p => p.Id);
        return Results.Ok(new AnswersWithPrompts([.. rows.Select(r => new AnswerWithPrompt(ToAnswer(r), prompts[r.PromptId].Text, prompts[r.PromptId].Day))]));
    }

    // Staff: the last period and the next eight weeks, each release day's prompt or none yet (a library prompt is chosen on the day).
    private static async Task<IResult> ListForStaffAsync(NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Prompt>(
            $"{PromptSelect} WHERE p.day BETWEEN prompt_week_day() - 7 AND prompt_week_day() + 56 ORDER BY p.day DESC");
        return Results.Ok(new PromptList([.. rows]));
    }

    private static async Task<IResult> ScheduleAsync(
        ClaimsPrincipal user, string day, ScheduleRequest? request, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var date = ParseDay(day);
        var text = request?.Text?.Trim();
        if (text is not { Length: > 0 } || text.Length > 200)
        {
            throw ApiProblemException.Validation(new FieldError("text", "Write 1 to 200 characters."));
        }

        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        // A release day: a Thursday on the weekly cadence, any day on the daily one (migration 0051).
        if (!await connection.ExecuteScalarAsync<bool>("SELECT prompt_week_day(@date) = @date", new { date }))
        {
            throw ApiProblemException.Validation(new FieldError("day", "Prompts come out on Thursdays: pick a Thursday."));
        }

        if (date < await connection.ExecuteScalarAsync<DateOnly>("SELECT prompt_week_day()"))
        {
            throw ApiProblemException.Validation(new FieldError("day", "Schedule the current prompt's day or a later one."));
        }

        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO prompts (day, text, source, created_by) VALUES (@day, @text, 'staff', @me)
                ON CONFLICT ON CONSTRAINT prompts_one_a_day DO UPDATE SET text = EXCLUDED.text, source = 'staff', created_by = EXCLUDED.created_by
                """,
                new { day = date, text, me = user.UserId() });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException && e.MessageText.Contains("have answered", StringComparison.Ordinal))
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already answered",
                "Fans have answered this week's prompt, so it can't change.");
        }

        return Results.Ok(await connection.QuerySingleAsync<Prompt>($"{PromptSelect} WHERE p.day = @day", new { day = date }));
    }

    private static async Task<IResult> UnscheduleAsync(string day, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var date = ParseDay(day);
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM prompt_answers a JOIN prompts p ON p.id = a.prompt_id WHERE p.day = @day)", new { day = date }))
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "conflict", "Already answered",
                "Fans have answered this day's prompt, so it stays.");
        }

        await connection.ExecuteAsync("DELETE FROM prompts WHERE day = @day", new { day = date });
        return Results.NoContent();
    }

    private static async Task<PromptToday> LoadTodayAsync(NpgsqlConnection connection, Guid me, Guid promptId)
    {
        var prompt = await connection.QuerySingleAsync<Prompt>($"{PromptSelect} WHERE p.id = @promptId", new { promptId });
        var mine = await connection.QuerySingleOrDefaultAsync<AnswerRow>(
            $"{AnswerSelect} WHERE pa.prompt_id = @promptId AND pa.user_id = @me AND pa.deleted_at IS NULL", new { promptId, me });
        // Others' answers once the caller has answered: nobody copies a band name they haven't thought of themselves.
        var following = mine is null
            ? []
            : (await connection.QueryAsync<AnswerRow>(
                $"""
                 {AnswerSelect}
                 WHERE pa.prompt_id = @promptId AND pa.user_id <> @me AND {Visible}
                   AND EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = pa.user_id)
                 ORDER BY pa.created_at DESC, pa.id LIMIT 3
                 """,
                new { promptId, me })).ToList();
        var next = await connection.ExecuteScalarAsync<DateOnly>("SELECT prompt_next_day(@day)", new { day = prompt.Day });
        return new PromptToday(prompt, mine is null ? null : ToAnswer(mine), [.. following.Select(ToAnswer)], next);
    }

    private static async Task RequireOpenAsync(NpgsqlConnection connection, Guid promptId)
    {
        var open = await connection.ExecuteScalarAsync<bool?>(
                       "SELECT day = prompt_week_day() FROM prompts WHERE id = @promptId", new { promptId })
                   ?? throw NotFound();
        if (!open)
        {
            throw Closed();
        }
    }

    private static DateOnly ParseDay(string day) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw ApiProblemException.Validation(new FieldError("day", "Use a date as YYYY-MM-DD."));

    private static ApiProblemException Closed() =>
        new(StatusCodes.Status409Conflict, "prompt_closed", "Prompt closed", "Answers are written in the prompt's week. This week's prompt is waiting.");

    private static ApiProblemException NotFound() =>
        new(StatusCodes.Status404NotFound, "not_found", "Not found", "No prompt has this id.");

    /// <summary>Needs alias <c>pa</c> and <c>@me</c>: live authors, not withdrawn or taken down (except one's own), visible to the caller.</summary>
    public const string Visible =
        """
        pa.deleted_at IS NULL AND (pa.user_id = @me OR (pa.removed_at IS NULL AND (pa.visibility = 'public'
          OR EXISTS (SELECT 1 FROM follows fv WHERE fv.follower_id = @me AND fv.followee_id = pa.user_id))))
        """;

    private const string PromptSelect =
        """
        SELECT p.id AS Id, p.day AS Day, p.text AS Text, p.source AS Source, p.day = prompt_week_day() AS Open,
               (SELECT count(*)::int FROM prompt_answers a JOIN users au ON au.id = a.user_id AND au.deleted_at IS NULL
                 WHERE a.prompt_id = p.id AND a.deleted_at IS NULL AND a.removed_at IS NULL) AS AnswerCount
        FROM prompts p
        """;

    private const string AnswerSelect =
        """
        SELECT pa.id AS Id, pa.prompt_id AS PromptId, u.handle::text AS Handle, u.display_name AS DisplayName, u.avatar_url AS AvatarUrl,
               pa.body AS Body, pa.visibility::text AS Visibility, pa.created_at AS CreatedAt, pa.updated_at AS UpdatedAt,
               pa.user_id = @me AS Mine,
               EXISTS (SELECT 1 FROM follows f WHERE f.follower_id = @me AND f.followee_id = pa.user_id) AS Following
        FROM prompt_answers pa JOIN users u ON u.id = pa.user_id AND u.deleted_at IS NULL
        """;

    private static PromptAnswer ToAnswer(AnswerRow r) =>
        new(r.Id, r.PromptId, new SubmissionEndpoints.UserRef(r.Handle, false, r.DisplayName, r.AvatarUrl), r.Body, r.Visibility,
            r.CreatedAt, r.UpdatedAt, r.Mine, r.Following);

    private sealed record AnswerRow(
        Guid Id, Guid PromptId, string Handle, string? DisplayName, string? AvatarUrl, string Body, string Visibility, DateTime CreatedAt,
        DateTime UpdatedAt, bool Mine, bool Following);
}
