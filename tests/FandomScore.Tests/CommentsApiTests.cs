using System.Net;
using System.Text.Json;
using Dapper;

namespace FandomScore.Tests;

/// <summary>Comments on album logs (openapi tag Comments): threads, points, caps, visibility, and tombstones.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CommentsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Substantive = "This record rewards patience; the second half is where it opens up.";

    private JournalTestHost _host = null!;
    private string _owner = null!;
    private string _other = null!;
    private string _logId = null!;

    public async Task InitializeAsync()
    {
        _host = await JournalTestHost.StartAsync(postgres, "comments_api");
        _owner = await _host.SignInAsync("fan_one");
        _other = await _host.SignInAsync("fan_two");
        _logId = await _host.LogAsync(_owner, _host.InRainbows);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Substantive_comments_on_others_logs_earn_points_and_threads_list_in_order()
    {
        var first = await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = Substantive }), HttpStatusCode.Created);
        Assert.Equal(["comment_posted"], first.GetProperty("points_awarded").EnumerateArray().Select(p => p.GetProperty("rule_key").GetString()));
        var firstId = first.GetProperty("comment").GetProperty("id").GetString()!;
        Assert.Equal("fan_two", first.GetProperty("comment").GetProperty("user").GetProperty("handle").GetString());

        // Short comments and the log owner's own comments earn nothing but are still posted.
        var brief = await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = "Agreed." }), HttpStatusCode.Created);
        Assert.Empty(brief.GetProperty("points_awarded").EnumerateArray());
        var reply = await _host.JsonAsync(
            await _host.PostAsync($"/v1/logs/{_logId}/comments", _owner, new { body = Substantive, parent_id = firstId }), HttpStatusCode.Created);
        Assert.Empty(reply.GetProperty("points_awarded").EnumerateArray());
        Assert.Equal(firstId, reply.GetProperty("comment").GetProperty("parent_id").GetString());

        var page = await _host.JsonAsync(await _host.GetAsync($"/v1/logs/{_logId}/comments", _owner), HttpStatusCode.OK);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([Substantive, "Agreed.", Substantive], items.Select(c => c.GetProperty("body").GetString()));
        Assert.All(items, c => Assert.False(c.GetProperty("deleted").GetBoolean()));

        var log = await _host.JsonAsync(await _host.GetAsync($"/v1/logs/{_logId}", _owner), HttpStatusCode.OK);
        Assert.Equal(3, log.GetProperty("comment_count").GetInt32());
    }

    [Fact]
    public async Task The_daily_cap_stops_awards_but_not_comments()
    {
        for (var i = 0; i < 6; i++)
        {
            var result = await _host.JsonAsync(
                await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = $"{Substantive} #{i}" }), HttpStatusCode.Created);
            Assert.Equal(i < 5 ? 1 : 0, result.GetProperty("points_awarded").GetArrayLength());
        }

        await using var connection = await _host.Db.OpenConnectionAsync();
        Assert.Equal(5, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM score_events WHERE rule_key = 'comment_posted' AND reversed_at IS NULL"));
    }

    [Fact]
    public async Task Replies_must_point_at_a_live_comment_on_the_same_log()
    {
        var otherLog = await _host.LogAsync(_owner, _host.TheBends);
        var elsewhere = await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{otherLog}/comments", _other, new { body = "Elsewhere" }), HttpStatusCode.Created);
        var elsewhereId = elsewhere.GetProperty("comment").GetProperty("id").GetString();

        var problem = await _host.JsonAsync(
            await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = "Reply", parent_id = elsewhereId }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("parent_id", problem.GetProperty("errors")[0].GetProperty("field").GetString());

        var empty = await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = "   " }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("body", empty.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Editing_keeps_points_and_deleting_reverses_them_leaving_a_tombstone_when_replied_to()
    {
        var first = await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = Substantive }), HttpStatusCode.Created);
        var firstId = first.GetProperty("comment").GetProperty("id").GetString()!;
        var lonely = await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = "Lonely" }), HttpStatusCode.Created);
        var lonelyId = lonely.GetProperty("comment").GetProperty("id").GetString()!;
        await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{_logId}/comments", _owner, new { body = "Reply", parent_id = firstId }), HttpStatusCode.Created);

        var edited = await _host.JsonAsync(await _host.SendAsync(HttpMethod.Patch, $"/v1/comments/{firstId}", _other, new { body = "Edited." }), HttpStatusCode.OK);
        Assert.Equal("Edited.", edited.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("edited_at").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await _host.SendAsync(HttpMethod.Delete, $"/v1/comments/{firstId}", _other)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _host.SendAsync(HttpMethod.Delete, $"/v1/comments/{lonelyId}", _other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.SendAsync(HttpMethod.Delete, $"/v1/comments/{lonelyId}", _other)).StatusCode);

        var page = await _host.JsonAsync(await _host.GetAsync($"/v1/logs/{_logId}/comments", _owner), HttpStatusCode.OK);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);   // the tombstone and the reply; the lonely comment is gone
        Assert.True(items[0].GetProperty("deleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("body").ValueKind);
        Assert.Equal("Reply", items[1].GetProperty("body").GetString());

        await using var connection = await _host.Db.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM score_events WHERE source_id = @firstId::uuid AND reversed_at IS NULL", new { firstId }));
    }

    [Fact]
    public async Task Comments_follow_the_logs_visibility_and_only_authors_edit()
    {
        var privateLog = await _host.LogAsync(_owner, _host.TheBends, visibility: "private");
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetAsync($"/v1/logs/{privateLog}/comments", _other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.PostAsync($"/v1/logs/{privateLog}/comments", _other, new { body = "Hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _host.PostAsync($"/v1/logs/{privateLog}/comments", _owner, new { body = "Note to self" })).StatusCode);

        var comment = await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = "Public" }), HttpStatusCode.Created);
        var commentId = comment.GetProperty("comment").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.SendAsync(HttpMethod.Patch, $"/v1/comments/{commentId}", _owner, new { body = "Mine now" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.SendAsync(HttpMethod.Delete, $"/v1/comments/{commentId}", _owner)).StatusCode);
    }

    [Fact]
    public async Task An_idempotency_key_replays_the_comment()
    {
        var body = new { body = Substantive };
        var first = await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, body, "c-1"), HttpStatusCode.Created);
        var again = await _host.JsonAsync(await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, body, "c-1"), HttpStatusCode.Created);
        Assert.Equal(first.GetProperty("comment").GetProperty("id").GetString(), again.GetProperty("comment").GetProperty("id").GetString());

        var page = await _host.JsonAsync(await _host.GetAsync($"/v1/logs/{_logId}/comments", _owner), HttpStatusCode.OK);
        Assert.Single(page.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Posting_is_rate_limited_per_user()
    {
        for (var i = 0; i < 60; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = $"c{i}" })).StatusCode);
        }

        var limited = await _host.PostAsync($"/v1/logs/{_logId}/comments", _other, new { body = "one too many" });
        var problem = await _host.JsonAsync(limited, HttpStatusCode.TooManyRequests);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());
        // Another fan is not affected.
        Assert.Equal(HttpStatusCode.Created, (await _host.PostAsync($"/v1/logs/{_logId}/comments", _owner, new { body = "still fine" })).StatusCode);
    }
}
