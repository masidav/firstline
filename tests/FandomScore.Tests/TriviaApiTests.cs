using FandomScore.Tests.Contract;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Daily music trivia (migration 0022): one question a day from the catalog, answered once, 0.30 points if right.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TriviaApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly string[] Kinds = ["track_album", "release_year"];

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"trivia_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();
        _artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 3);
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            UPDATE albums SET release_date = make_date(2018 + (right(spotify_album_id, 1))::int, 5, 1) WHERE artist_id = @_artistId;
            INSERT INTO tracks (album_id, title, track_number)
              SELECT al.id, 'Song ' || al.spotify_album_id || '-' || n, n FROM albums al, generate_series(1, 4) n WHERE al.artist_id = @_artistId;
            """,
            new { _artistId });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task One_question_a_day_answered_once_and_a_right_answer_earns_a_tenth_of_a_log()
    {
        var ana = await FanAsync("ana", scored: true);
        var today = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", ana), HttpStatusCode.OK);
        var question = today.GetProperty("question");
        // Weekly since 0034: the question is this week's Tuesday's, and the next one comes a week later.
        var day = DateOnly.Parse(question.GetProperty("day").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DayOfWeek.Tuesday, day.DayOfWeek);
        Assert.InRange(DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - day.DayNumber, 0, 6);
        Assert.Equal(day.AddDays(7).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), today.GetProperty("next_question_on").GetString());
        Assert.Equal("Geese", question.GetProperty("artist").GetProperty("name").GetString());
        Assert.Contains(question.GetProperty("kind").GetString(), Kinds);
        Assert.InRange(question.GetProperty("options").GetArrayLength(), 2, 4);
        Assert.Equal((false, JsonValueKind.Null, 0.30m), (question.GetProperty("answered").GetBoolean(),
            question.GetProperty("answer_index").ValueKind, question.GetProperty("points_for_right").GetDecimal()));
        var id = question.GetProperty("id").GetString();

        // Asking again gives the same question.
        Assert.Equal(id, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", ana), HttpStatusCode.OK)).GetProperty("question").GetProperty("id").GetString());

        int right;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            right = await connection.ExecuteScalarAsync<int>("SELECT answer_index FROM trivia_questions WHERE id = @id::uuid", new { id });
        }

        Assert.Equal("choice", (await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/trivia/{id}/answer", ana, new { }), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors")[0].GetProperty("field").GetString());
        var answered = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/trivia/{id}/answer", ana, new { choice = right }), HttpStatusCode.OK);
        Assert.Equal((true, true, right, 0.30m), (answered.GetProperty("answered").GetBoolean(), answered.GetProperty("correct").GetBoolean(),
            answered.GetProperty("answer_index").GetInt32(), answered.GetProperty("points_earned").GetDecimal()));
        var again = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/trivia/{id}/answer", ana, new { choice = right }), HttpStatusCode.Conflict);
        Assert.Equal("trivia_answered", again.GetProperty("code").GetString());

        // Someone else's question isn't theirs to answer; a wrong answer earns nothing.
        var ben = await FanAsync("ben", scored: true);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/v1/me/trivia/{id}/answer", ben, new { choice = 0 })).StatusCode);
        var benQuestion = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", ben), HttpStatusCode.OK)).GetProperty("question");
        var benId = benQuestion.GetProperty("id").GetString();
        int benRight;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            benRight = await connection.ExecuteScalarAsync<int>("SELECT answer_index FROM trivia_questions WHERE id = @benId::uuid", new { benId });
        }

        var wrong = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/trivia/{benId}/answer", ben, new { choice = (benRight + 1) % 2 }), HttpStatusCode.OK);
        Assert.Equal((false, 0m), (wrong.GetProperty("correct").GetBoolean(), wrong.GetProperty("points_earned").GetDecimal()));

        await using (var connection = await _db.OpenConnectionAsync())
        {
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM score_events WHERE rule_key = 'trivia_correct' AND reversed_at IS NULL"));
        }
    }

    [Fact]
    public async Task Fans_of_an_artist_share_the_question_and_see_who_they_follow_got_it_right_and_the_percent()
    {
        var ana = await FanAsync("ana", scored: true);
        var fans = new Dictionary<string, string>();
        foreach (var handle in new[] { "ben", "cat", "dee", "eve" })
        {
            fans[handle] = await FanAsync(handle, scored: true);
            await SendAsync(HttpMethod.Put, $"/v1/users/{handle}/follow", ana);
        }

        var question = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", ana), HttpStatusCode.OK)).GetProperty("question");
        Assert.Equal(JsonValueKind.Null, question.GetProperty("results").ValueKind);   // nothing before answering
        var right = await AnswerIndexAsync(question.GetProperty("id").GetString()!);

        // Everyone gets the same question; ben, cat, and dee answer right (in that order), eve wrong.
        foreach (var (handle, correct) in new[] { ("ben", true), ("cat", true), ("dee", true), ("eve", false) })
        {
            var theirs = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", fans[handle]), HttpStatusCode.OK)).GetProperty("question");
            Assert.Equal(question.GetProperty("prompt").GetString(), theirs.GetProperty("prompt").GetString());
            Assert.Equal(question.GetProperty("options").GetRawText(), theirs.GetProperty("options").GetRawText());
            await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/trivia/{theirs.GetProperty("id").GetString()}/answer", fans[handle],
                new { choice = correct ? right : (right + 1) % 2 }), HttpStatusCode.OK);
        }

        var answered = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/trivia/{question.GetProperty("id").GetString()}/answer", ana,
            new { choice = (right + 1) % 2 }), HttpStatusCode.OK);
        var results = answered.GetProperty("results");
        Assert.Equal((5, 3, 60), (results.GetProperty("answered").GetInt32(), results.GetProperty("right").GetInt32(),
            results.GetProperty("percent_right").GetInt32()));
        Assert.Equal(["ben", "cat"], results.GetProperty("following_right").EnumerateArray().Select(u => u.GetProperty("handle").GetString()));

        // Results stay on the question when it's read again.
        var again = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", ana), HttpStatusCode.OK)).GetProperty("question");
        Assert.Equal(60, again.GetProperty("results").GetProperty("percent_right").GetInt32());
    }

    private async Task<int> AnswerIndexAsync(string questionId)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT answer_index FROM trivia_questions WHERE id = @questionId::uuid", new { questionId });
    }

    [Fact]
    public async Task A_fan_without_scored_artists_gets_no_question()
    {
        var cat = await FanAsync("cat", scored: false);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", cat), HttpStatusCode.OK))
            .GetProperty("question").ValueKind);
    }

    [Fact]
    public async Task An_unanswered_question_stays_until_answered_and_its_results_stay_until_the_next_Tuesday()
    {
        var ana = await FanAsync("ana", scored: true);
        Guid old;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            // Last week's shared question for Geese, and Ana's copy of it, which she never answered.
            old = await connection.ExecuteScalarAsync<Guid>(
                """
                WITH shared AS (
                  INSERT INTO trivia_daily_questions (day, artist_id, kind, prompt, options, answer_index)
                  VALUES (trivia_week_day() - 7, @_artistId, 'release_year', 'When did Geese put out Projector?', ARRAY['2021', '2023'], 0)
                  RETURNING *)
                INSERT INTO trivia_questions (user_id, day, artist_id, kind, prompt, options, answer_index, daily_question_id)
                SELECT u.id, s.day, s.artist_id, s.kind, s.prompt, s.options, s.answer_index, s.id
                FROM users u, shared s WHERE u.handle = 'ana' RETURNING id
                """,
                new { _artistId });
        }

        // Last week's question is still Ana's question, and still answerable, for the same 0.30 points.
        var carried = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", ana), HttpStatusCode.OK)).GetProperty("question");
        Assert.Equal((old.ToString(), false), (carried.GetProperty("id").GetString(), carried.GetProperty("answered").GetBoolean()));
        var answered = await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/me/trivia/{old}/answer", ana, new { choice = 0 }), HttpStatusCode.OK);
        Assert.Equal((true, 0.30m), (answered.GetProperty("correct").GetBoolean(), answered.GetProperty("points_earned").GetDecimal()));

        // Answered: its results show until the next Tuesday, instead of a new question.
        var after = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", ana), HttpStatusCode.OK)).GetProperty("question");
        Assert.Equal((old.ToString(), true), (after.GetProperty("id").GetString(), after.GetProperty("answered").GetBoolean()));
        Assert.Equal(JsonValueKind.Object, after.GetProperty("results").ValueKind);

        // Once a Tuesday has passed since the answer, this week's question comes.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                ALTER TABLE trivia_questions DISABLE TRIGGER trg_trivia_guard;
                UPDATE trivia_questions SET answered_at = trivia_week_day()::timestamp AT TIME ZONE 'UTC' - interval '1 day' WHERE id = @old;
                ALTER TABLE trivia_questions ENABLE TRIGGER trg_trivia_guard;
                """,
                new { old });
        }

        var next = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me/trivia", ana), HttpStatusCode.OK)).GetProperty("question");
        Assert.NotEqual(old.ToString(), next.GetProperty("id").GetString());
        Assert.False(next.GetProperty("answered").GetBoolean());
    }

    private async Task<string> FanAsync(string handle, bool scored)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        var token = (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
        if (scored)
        {
            await using var connection = await _db.OpenConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight)
                SELECT id, @_artistId, 50, 50, false, 0.1 FROM users WHERE handle = @handle
                """,
                new { _artistId, handle });
        }

        return token;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }
}
