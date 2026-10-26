using System.Security.Claims;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Infrastructure;
using Npgsql;

namespace FandomScore.Api.Features.Trivia;

/// <summary>
/// Daily music trivia (migrations 0022 and 0024, decided 2026-10-25): one question a day per fan about one of their
/// scored artists. Fans of the same artist share the day's question (made from the stored catalog by the first fan who
/// asks), so once a fan answers they see one or two people they follow who got it right and the percent of fans who
/// did. A right answer earns <c>trivia_correct</c> (0.30 points, a tenth of logging an album) through
/// <c>answer_trivia()</c>. Neither the answer nor the results are sent before the fan answers.
/// </summary>
public static class TriviaEndpoints
{
    public static void MapTrivia(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me/trivia", GetTodayAsync);
        v1.MapPost("/me/trivia/{questionId:guid}/answer", AnswerAsync);
    }

    /// <summary>openapi TriviaQuestion. <c>AnswerIndex</c>, <c>Correct</c>, and <c>Results</c> only once answered.</summary>
    public sealed record TriviaQuestion(
        Guid Id, DateOnly Day, ScoringEndpoints.ArtistRef Artist, string Kind, string Prompt, IReadOnlyList<string> Options,
        bool Answered, int? ChosenIndex, int? AnswerIndex, bool? Correct, decimal PointsForRight, decimal? PointsEarned,
        TriviaResults? Results);

    /// <summary>
    /// How the day's shared question went so far: fans who answered it, how many got it right (the caller included),
    /// and up to two people the caller follows who got it right, first to answer first.
    /// </summary>
    public sealed record TriviaResults(int Answered, int Right, int PercentRight, IReadOnlyList<SubmissionEndpoints.UserRef> FollowingRight);

    /// <summary><c>Question</c> is null when the fan has no artist with enough catalog to ask about yet.</summary>
    public sealed record TriviaToday(TriviaQuestion? Question);

    public sealed record AnswerRequest(int? Choice);

    private static async Task<IResult> GetTodayAsync(ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var existing = await LoadTodayAsync(connection, me);
        if (existing is not null)
        {
            return Results.Ok(new TriviaToday(existing));
        }

        if (await SharedQuestionAsync(connection, me) is not { } shared)
        {
            return Results.Ok(new TriviaToday(null));
        }

        // The fan's copy of the shared question. Two requests at once: the first insert wins and both read it back.
        await connection.ExecuteAsync(
            """
            INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index, daily_question_id)
            SELECT @me, d.day, d.artist_id, d.kind, d.prompt, d.options, d.answer_index, d.id FROM trivia_daily_questions d WHERE d.id = @shared
            ON CONFLICT ON CONSTRAINT trivia_questions_one_a_day DO NOTHING
            """,
            new { me, shared });
        return Results.Ok(new TriviaToday(await LoadTodayAsync(connection, me)));
    }

    private static async Task<IResult> AnswerAsync(
        ClaimsPrincipal user, Guid questionId, AnswerRequest? request, NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        if (request?.Choice is not { } choice)
        {
            throw ApiProblemException.Validation(new FieldError("choice", "Send the index of the option you pick."));
        }

        var me = user.UserId();
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await connection.ExecuteAsync("SELECT answer_trivia(@me, @questionId, @choice)", new { me, questionId, choice }, transaction);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException
                                              && e.MessageText.StartsWith("trivia blocked: ", StringComparison.Ordinal))
            {
                throw e.MessageText["trivia blocked: ".Length..] switch
                {
                    "answered" => new ApiProblemException(StatusCodes.Status409Conflict, "trivia_answered", "Already answered",
                        "You've answered today's question. Come back tomorrow."),
                    "expired" => new ApiProblemException(StatusCodes.Status409Conflict, "trivia_expired", "Question expired",
                        "That was another day's question. Today's is waiting."),
                    "choice" => ApiProblemException.Validation(new FieldError("choice", "Pick one of the options.")),
                    _ => new ApiProblemException(StatusCodes.Status404NotFound, "not_found", "Not found", "No question of yours has this id."),
                };
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return Results.Ok(await LoadAsync(connection, me, questionId));
    }

    private const string QuestionSelect =
        """
        SELECT q.id AS Id, q.day AS Day, a.id AS ArtistId, a.name AS ArtistName, a.image_url AS ArtistImageUrl, q.kind AS Kind,
               q.prompt AS Prompt, q.options AS Options, q.answered_at IS NOT NULL AS Answered, q.chosen_index AS ChosenIndex,
               q.answer_index AS AnswerIndex, q.correct AS Correct,
               (SELECT points FROM scoring_rules WHERE rule_key = 'trivia_correct') AS PointsForRight,
               (SELECT sum(points) FROM score_events se WHERE se.source_type = 'trivia' AND se.source_id = q.id AND se.reversed_at IS NULL) AS PointsEarned,
               q.daily_question_id AS DailyQuestionId
        FROM trivia_questions q JOIN artists a ON a.id = q.artist_id
        """;

    private static async Task<TriviaQuestion?> LoadTodayAsync(NpgsqlConnection connection, Guid me)
    {
        var row = await connection.QuerySingleOrDefaultAsync<QuestionRow>(
            $"{QuestionSelect} WHERE q.user_id = @me AND q.day = (now() AT TIME ZONE 'UTC')::date", new { me });
        return row is null ? null : await ToQuestionAsync(connection, me, row);
    }

    private static async Task<TriviaQuestion> LoadAsync(NpgsqlConnection connection, Guid me, Guid id) =>
        await ToQuestionAsync(connection, me,
            await connection.QuerySingleAsync<QuestionRow>($"{QuestionSelect} WHERE q.user_id = @me AND q.id = @id", new { me, id }));

    private static async Task<TriviaQuestion> ToQuestionAsync(NpgsqlConnection connection, Guid me, QuestionRow r) =>
        new(r.Id, r.Day, new ScoringEndpoints.ArtistRef(r.ArtistId, r.ArtistName, r.ArtistImageUrl), r.Kind, r.Prompt,
            r.Options, r.Answered, r.ChosenIndex, r.Answered ? r.AnswerIndex : null, r.Correct, r.PointsForRight ?? 0,
            r.Answered ? r.PointsEarned ?? 0 : null,
            r.Answered && r.DailyQuestionId is { } shared ? await ResultsAsync(connection, me, shared) : null);

    // Live accounts that answered the shared question; the people the caller follows who got it right, first first.
    private static async Task<TriviaResults> ResultsAsync(NpgsqlConnection connection, Guid me, Guid shared)
    {
        var (answered, right) = await connection.QuerySingleAsync<(int, int)>(
            """
            SELECT count(*)::int, count(*) FILTER (WHERE t.correct)::int
            FROM trivia_questions t JOIN users u ON u.id = t.user_id AND u.deleted_at IS NULL
            WHERE t.daily_question_id = @shared AND t.answered_at IS NOT NULL
            """,
            new { shared });
        var following = await connection.QueryAsync<SubmissionEndpoints.UserRef>(
            """
            SELECT u.handle::text AS Handle, false AS Deleted, u.display_name AS DisplayName, u.avatar_url AS AvatarUrl
            FROM trivia_questions t
            JOIN follows f ON f.followee_id = t.user_id AND f.follower_id = @me
            JOIN users u ON u.id = t.user_id AND u.deleted_at IS NULL
            WHERE t.daily_question_id = @shared AND t.correct
            ORDER BY t.answered_at, u.id
            LIMIT 2
            """,
            new { me, shared });
        return new TriviaResults(answered, right, answered == 0 ? 0 : (int)Math.Round(100.0 * right / answered), [.. following]);
    }

    // Today's shared question for one of the fan's active scored artists: the one another fan already started for
    // that artist, or a new one made from the catalog (the first insert wins). Artists are tried in random order;
    // null when none has enough catalog.
    private static async Task<Guid?> SharedQuestionAsync(NpgsqlConnection connection, Guid me)
    {
        var existing = await connection.QueryAsync<Guid>(
            """
            SELECT d.id FROM trivia_daily_questions d
            JOIN user_artist_scores s ON s.artist_id = d.artist_id AND s.user_id = @me
            JOIN artists a ON a.id = d.artist_id AND a.status = 'active'
            WHERE d.day = (now() AT TIME ZONE 'UTC')::date
            """,
            new { me });
        // Prefer joining a question other fans are already playing: that is what makes the results worth seeing.
        if (existing.OrderBy(_ => Random.Shared.Next()).FirstOrDefault() is var started && started != Guid.Empty)
        {
            return started;
        }

        if (await MakeQuestionAsync(connection, me) is not { } made)
        {
            return null;
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO trivia_daily_questions (day, artist_id, kind, prompt, options, answer_index)
            VALUES ((now() AT TIME ZONE 'UTC')::date, @ArtistId, @Kind, @Prompt, @Options, @AnswerIndex)
            ON CONFLICT ON CONSTRAINT trivia_daily_one_per_artist DO NOTHING
            """,
            new { made.ArtistId, made.Kind, made.Prompt, Options = made.Options.ToArray(), made.AnswerIndex });
        return await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM trivia_daily_questions WHERE day = (now() AT TIME ZONE 'UTC')::date AND artist_id = @ArtistId",
            new { made.ArtistId });
    }

    // A question about one of the fan's active scored artists, from what the catalog has: which album a song is on
    // (needs two albums), or the year an album came out (any dated album). Null when nothing fits.
    private static async Task<Made?> MakeQuestionAsync(NpgsqlConnection connection, Guid me)
    {
        var albums = (await connection.QueryAsync<AlbumRow>(
            """
            SELECT a.id AS ArtistId, a.name AS ArtistName, al.id AS AlbumId, al.title AS Title, al.release_date AS ReleaseDate,
                   al.release_date_precision AS Precision
            FROM user_artist_scores s JOIN artists a ON a.id = s.artist_id AND a.status = 'active'
            JOIN albums al ON al.artist_id = a.id AND al.delisted_at IS NULL
            WHERE s.user_id = @me
            """,
            new { me })).ToList();
        if (albums.Count == 0)
        {
            return null;
        }

        var random = Random.Shared;
        foreach (var artist in albums.GroupBy(a => a.ArtistId).OrderBy(_ => random.Next()))
        {
            var distinct = artist.GroupBy(a => a.Title.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var kinds = new List<string>();
            if (distinct.Count >= 2)
            {
                kinds.Add("track_album");
            }

            if (distinct.Any(a => a.ReleaseDate is not null))
            {
                kinds.Add("release_year");
            }

            foreach (var kind in kinds.OrderBy(_ => random.Next()))
            {
                var made = kind == "track_album"
                    ? await TrackAlbumAsync(connection, artist.First().ArtistName, distinct, random)
                    : ReleaseYear(artist.First().ArtistName, distinct, random);
                if (made is not null)
                {
                    return made with { ArtistId = artist.Key };
                }
            }
        }

        return null;
    }

    private static async Task<Made?> TrackAlbumAsync(NpgsqlConnection connection, string artistName, List<AlbumRow> albums, Random random)
    {
        var titles = albums.Select(a => a.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tracks = (await connection.QueryAsync<(Guid AlbumId, string Title)>(
            "SELECT album_id, title FROM tracks WHERE album_id = ANY(@ids)", new { ids = albums.Select(a => a.AlbumId).ToArray() }))
            // A title track would give the answer away; a song on two albums has two right answers.
            .Where(t => !titles.Contains(t.Title))
            .GroupBy(t => t.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).Select(g => g.First())
            .ToList();
        if (tracks.Count == 0)
        {
            return null;
        }

        var track = tracks[random.Next(tracks.Count)];
        var right = albums.First(a => a.AlbumId == track.AlbumId).Title;
        var options = albums.Where(a => a.AlbumId != track.AlbumId).OrderBy(_ => random.Next()).Take(3).Select(a => a.Title).Append(right)
            .OrderBy(_ => random.Next()).ToList();
        return new Made(Guid.Empty, "track_album", $"Which {artistName} album is \"{track.Title}\" on?", options, options.IndexOf(right));
    }

    private static Made? ReleaseYear(string artistName, List<AlbumRow> albums, Random random)
    {
        var dated = albums.Where(a => a.ReleaseDate is not null).ToList();
        var album = dated[random.Next(dated.Count)];
        var year = album.ReleaseDate!.Value.Year;
        var thisYear = DateTime.UtcNow.Year;
        var wrong = Enumerable.Range(year - 5, 11).Where(y => y != year && y <= thisYear).OrderBy(_ => random.Next()).Take(3);
        var options = wrong.Append(year).OrderBy(y => y).Select(y => y.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return new Made(Guid.Empty, "release_year", $"What year did {artistName} release \"{album.Title}\"?", options,
            options.IndexOf(year.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private sealed record Made(Guid ArtistId, string Kind, string Prompt, IReadOnlyList<string> Options, int AnswerIndex);

    private sealed record AlbumRow(Guid ArtistId, string ArtistName, Guid AlbumId, string Title, DateOnly? ReleaseDate, string Precision);

    private sealed class QuestionRow
    {
        public Guid Id { get; set; }
        public DateOnly Day { get; set; }
        public Guid ArtistId { get; set; }
        public string ArtistName { get; set; } = "";
        public string? ArtistImageUrl { get; set; }
        public string Kind { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string[] Options { get; set; } = [];
        public bool Answered { get; set; }
        public short? ChosenIndex { get; set; }
        public short AnswerIndex { get; set; }
        public bool? Correct { get; set; }
        public decimal? PointsForRight { get; set; }
        public decimal? PointsEarned { get; set; }
        public Guid? DailyQuestionId { get; set; }
    }
}
