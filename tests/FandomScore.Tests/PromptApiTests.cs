using System.Globalization;
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

/// <summary>Music prompts (migration 0025): the daily prompt, answers, the feed, reports and takedowns, staff scheduling.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PromptApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    private static string Day(int offset) =>
        DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"prompts_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Everyone_gets_the_days_prompt_answers_once_and_sees_who_they_follow_after_answering()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var cat = await SignInAsync("cat");
        await SendAsync(HttpMethod.Put, "/v1/users/ben/follow", ana);

        var today = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/prompts/today", ana), HttpStatusCode.OK);
        var prompt = today.GetProperty("prompt");
        Assert.Equal(("library", true, 0), (prompt.GetProperty("source").GetString(), prompt.GetProperty("open").GetBoolean(),
            prompt.GetProperty("answer_count").GetInt32()));
        var id = prompt.GetProperty("id").GetString();
        Assert.Equal(id, (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/prompts/today", cat), HttpStatusCode.OK)).GetProperty("prompt").GetProperty("id").GetString());

        // Ben answers followers-only; Cat answers publicly.
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{id}/answer", ben, new { body = "  The Velvet Ropes  ", visibility = "followers" }), HttpStatusCode.OK);
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{id}/answer", cat, new { body = "Bouncer Door" }), HttpStatusCode.OK);

        // Ana sees nobody's answers until she answers; then Ben's (she follows him).
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/prompts/today", ana), HttpStatusCode.OK)).GetProperty("following_answers").EnumerateArray());
        var answered = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{id}/answer", ana, new { body = "Face Value" }), HttpStatusCode.OK);
        Assert.Equal(("Face Value", "public", true), (answered.GetProperty("my_answer").GetProperty("body").GetString(),
            answered.GetProperty("my_answer").GetProperty("visibility").GetString(), answered.GetProperty("my_answer").GetProperty("mine").GetBoolean()));
        Assert.Equal(["The Velvet Ropes"], answered.GetProperty("following_answers").EnumerateArray().Select(a => a.GetProperty("body").GetString()));
        Assert.Equal(3, answered.GetProperty("prompt").GetProperty("answer_count").GetInt32());

        // Editing keeps one answer; the list puts hers first, then people she follows, then the rest.
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{id}/answer", ana, new { body = "Face Value Only" }), HttpStatusCode.OK);
        var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/prompts/{id}/answers", ana), HttpStatusCode.OK);
        Assert.Equal(["Face Value Only", "The Velvet Ropes", "Bouncer Door"], list.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("body").GetString()));
        // Cat doesn't follow Ben: his followers-only answer isn't hers to see, here or on the feed.
        var catList = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/prompts/{id}/answers", cat), HttpStatusCode.OK);
        Assert.Equal(["Bouncer Door", "Face Value Only"], catList.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("body").GetString()));

        var anaFeed = await FeedAnswersAsync(ana);
        Assert.Equal(["Bouncer Door", "Face Value Only", "The Velvet Ropes"], anaFeed.Order());
        Assert.DoesNotContain("The Velvet Ropes", await FeedAnswersAsync(cat));

        // Withdrawing removes it everywhere; answering again brings it back.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/prompts/{id}/answer", cat)).StatusCode);
        Assert.DoesNotContain("Bouncer Door", await FeedAnswersAsync(ana));
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{id}/answer", cat, new { body = "Bouncer Door Again" }), HttpStatusCode.OK);
        Assert.Contains("Bouncer Door Again", await FeedAnswersAsync(ana));

        Assert.Equal("body", (await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{id}/answer", ana, new { body = new string('x', 281) }), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Equal("visibility", (await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{id}/answer", ana, new { body = "x", visibility = "private" }), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Answers_can_be_reported_and_taken_down_and_old_prompts_are_closed()
    {
        var ana = await SignInAsync("ana");
        var ben = await SignInAsync("ben");
        var staff = await StaffAsync("staffer");
        var id = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/prompts/today", ana), HttpStatusCode.OK)).GetProperty("prompt").GetProperty("id").GetString();
        var answerId = (await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{id}/answer", ben, new { body = "Rude band name" }), HttpStatusCode.OK))
            .GetProperty("my_answer").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/v1/reports", ana,
            new { target_type = "prompt_answer", target_id = answerId, reason = "abuse" })).StatusCode);
        var queue = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/reports", staff), HttpStatusCode.OK);
        var post = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal(("prompt_answer", "Rude band name", JsonValueKind.Null), (post.GetProperty("target_type").GetString(),
            post.GetProperty("prompt_answer").GetProperty("body").GetString(), post.GetProperty("artist").ValueKind));
        await JsonAsync(await SendAsync(HttpMethod.Post, $"/v1/admin/reports/prompt_answer/{answerId}/decision", staff, new { action = "take_down" }),
            HttpStatusCode.OK);
        Assert.DoesNotContain("Rude band name", await FeedAnswersAsync(ana));
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/prompts/{id}/answers", ana), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        // The author still sees it as theirs; editing doesn't bring it back.
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{id}/answer", ben, new { body = "Nicer name" }), HttpStatusCode.OK);
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/prompts/{id}/answers", ana), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());

        // Yesterday's prompt is closed.
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("INSERT INTO prompts (day, text, source) VALUES ((now() AT TIME ZONE 'UTC')::date - 1, 'Yesterday?', 'staff')");
        }

        var yesterday = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/prompts", staff), HttpStatusCode.OK);
        var oldId = yesterday.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("text").GetString() == "Yesterday?").GetProperty("id").GetString();
        Assert.Equal("prompt_closed", (await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{oldId}/answer", ana, new { body = "Late" }), HttpStatusCode.Conflict))
            .GetProperty("code").GetString());
    }

    [Fact]
    public async Task Staff_schedule_prompts_ahead_and_fans_get_them_on_the_day()
    {
        var fan = await SignInAsync("fan");
        var staff = await StaffAsync("staffer");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/v1/admin/prompts/{Day(1)}", fan, new { text = "No" })).StatusCode);
        var scheduled = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/admin/prompts/{Day(1)}", staff, new { text = "Best tour merch ever?" }), HttpStatusCode.OK);
        Assert.Equal(("staff", false), (scheduled.GetProperty("source").GetString(), scheduled.GetProperty("open").GetBoolean()));
        Assert.Equal("day", (await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/admin/prompts/{Day(-1)}", staff, new { text = "Past" }), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors")[0].GetProperty("field").GetString());

        // Today's prompt can be replaced until someone answers it.
        var replaced = await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/admin/prompts/{Day(0)}", staff, new { text = "Staff pick today?" }), HttpStatusCode.OK);
        var today = (await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/prompts/today", fan), HttpStatusCode.OK)).GetProperty("prompt");
        Assert.Equal(("Staff pick today?", replaced.GetProperty("id").GetString()), (today.GetProperty("text").GetString(), today.GetProperty("id").GetString()));
        await JsonAsync(await SendAsync(HttpMethod.Put, $"/v1/prompts/{today.GetProperty("id").GetString()}/answer", fan, new { body = "Mine" }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, $"/v1/admin/prompts/{Day(0)}", staff, new { text = "Too late" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Delete, $"/v1/admin/prompts/{Day(0)}", staff)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/admin/prompts/{Day(1)}", staff)).StatusCode);
        var listed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/admin/prompts", staff), HttpStatusCode.OK);
        Assert.Equal(["Staff pick today?"], listed.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("text").GetString()));
    }

    private async Task<List<string>> FeedAnswersAsync(string token)
    {
        var feed = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/feed?limit=100", token), HttpStatusCode.OK);
        return [.. feed.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("type").GetString() == "prompt_answer")
            .Select(i => i.GetProperty("prompt_answer").GetProperty("body").GetString()!)];
    }

    private async Task<string> StaffAsync(string handle)
    {
        var token = await SignInAsync(handle);
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE users SET is_staff = true WHERE handle = @handle", new { handle });
        return token;
    }

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
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
