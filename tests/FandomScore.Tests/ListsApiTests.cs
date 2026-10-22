using System.Net;
using System.Text.Json;

namespace FandomScore.Tests;

/// <summary>Ordered album lists (openapi tag Lists): ownership, ordering, duplicates, visibility, and validation.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ListsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private JournalTestHost _host = null!;
    private string _owner = null!;
    private string _other = null!;

    public async Task InitializeAsync()
    {
        _host = await JournalTestHost.StartAsync(postgres, "lists_api");
        _owner = await _host.SignInAsync("fan_one");
        _other = await _host.SignInAsync("fan_two");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_list_can_be_created_read_edited_and_deleted()
    {
        var created = await _host.JsonAsync(await _host.PostAsync("/v1/lists", _owner, new { title = "  Desert island  ", description = "Ten records." }), HttpStatusCode.Created);
        var listId = created.GetProperty("id").GetString()!;
        Assert.Equal(("Desert island", "Ten records.", "public", 0, "fan_one"),
            (created.GetProperty("title").GetString(), created.GetProperty("description").GetString(), created.GetProperty("visibility").GetString(),
             created.GetProperty("item_count").GetInt32(), created.GetProperty("user").GetProperty("handle").GetString()));

        var mine = await _host.JsonAsync(await _host.GetAsync("/v1/me/lists", _owner), HttpStatusCode.OK);
        Assert.Equal([listId], mine.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("id").GetString()));

        var detail = await _host.JsonAsync(await _host.GetAsync($"/v1/lists/{listId}", _other), HttpStatusCode.OK);
        Assert.Empty(detail.GetProperty("items").EnumerateArray());

        var edited = await _host.JsonAsync(
            await _host.SendAsync(HttpMethod.Patch, $"/v1/lists/{listId}", _owner, new { title = "Island", description = (string?)null, visibility = "private" }), HttpStatusCode.OK);
        Assert.Equal(("Island", JsonValueKind.Null, "private"),
            (edited.GetProperty("title").GetString(), edited.GetProperty("description").ValueKind, edited.GetProperty("visibility").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetAsync($"/v1/lists/{listId}", _other)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _host.SendAsync(HttpMethod.Delete, $"/v1/lists/{listId}", _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetAsync($"/v1/lists/{listId}", _owner)).StatusCode);
    }

    [Fact]
    public async Task Items_keep_contiguous_positions_through_inserts_moves_and_removals()
    {
        var listId = await CreateListAsync();
        await using var connection = await _host.Db.OpenConnectionAsync();
        var third = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(connection,
            "INSERT INTO albums (artist_id, title, spotify_album_id, album_type) VALUES (@ArtistId, 'Kid A', 'al-kid-a', 'album') RETURNING id",
            new { _host.ArtistId });

        await Add(listId, _host.InRainbows);                          // [Rainbows]
        var two = await Add(listId, _host.TheBends, note: "Peak era");  // [Rainbows, Bends]
        Assert.Equal(["In Rainbows", "The Bends"], Titles(two));
        var three = await Add(listId, third, position: 1);          // [Kid A, Rainbows, Bends]
        Assert.Equal(["Kid A", "In Rainbows", "The Bends"], Titles(three));
        Assert.Equal([1, 2, 3], three.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("position").GetInt32()));

        var duplicate = await _host.JsonAsync(await _host.PostAsync($"/v1/lists/{listId}/items", _owner, new { album_id = _host.InRainbows }), HttpStatusCode.Conflict);
        Assert.Equal("conflict", duplicate.GetProperty("code").GetString());

        var moved = await _host.JsonAsync(
            await _host.SendAsync(HttpMethod.Patch, $"/v1/lists/{listId}/items/{_host.TheBends}", _owner, new { position = 1, note = "First" }), HttpStatusCode.OK);
        Assert.Equal(["The Bends", "Kid A", "In Rainbows"], Titles(moved));
        Assert.Equal("First", moved.GetProperty("items")[0].GetProperty("note").GetString());
        var movedDown = await _host.JsonAsync(
            await _host.SendAsync(HttpMethod.Patch, $"/v1/lists/{listId}/items/{_host.TheBends}", _owner, new { position = 99 }), HttpStatusCode.OK);
        Assert.Equal(["Kid A", "In Rainbows", "The Bends"], Titles(movedDown));

        Assert.Equal(HttpStatusCode.NoContent, (await _host.SendAsync(HttpMethod.Delete, $"/v1/lists/{listId}/items/{third}", _owner)).StatusCode);
        var after = await _host.JsonAsync(await _host.GetAsync($"/v1/lists/{listId}", _owner), HttpStatusCode.OK);
        Assert.Equal(["In Rainbows", "The Bends"], Titles(after));
        Assert.Equal([1, 2], after.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("position").GetInt32()));
        Assert.Equal(2, after.GetProperty("item_count").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.SendAsync(HttpMethod.Delete, $"/v1/lists/{listId}/items/{third}", _owner)).StatusCode);
    }

    [Fact]
    public async Task Unknown_and_delisted_albums_cannot_be_added()
    {
        var listId = await CreateListAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await _host.PostAsync($"/v1/lists/{listId}/items", _owner, new { album_id = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.PostAsync($"/v1/lists/{listId}/items", _owner, new { album_id = _host.LostEp })).StatusCode);
    }

    [Fact]
    public async Task Only_owners_change_lists_and_private_lists_are_hidden()
    {
        var listId = await CreateListAsync();
        var secret = (await _host.JsonAsync(await _host.PostAsync("/v1/lists", _owner, new { title = "Secret", visibility = "private" }), HttpStatusCode.Created))
            .GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await _host.PostAsync($"/v1/lists/{listId}/items", _other, new { album_id = _host.InRainbows })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.SendAsync(HttpMethod.Patch, $"/v1/lists/{listId}", _other, new { title = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.SendAsync(HttpMethod.Delete, $"/v1/lists/{listId}", _other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.SendAsync(HttpMethod.Patch, $"/v1/lists/{secret}", _other, new { title = "Mine" })).StatusCode);

        var visible = await _host.JsonAsync(await _host.GetAsync("/v1/users/fan_one/lists", _other), HttpStatusCode.OK);
        Assert.Equal([listId], visible.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("id").GetString()));
        var own = await _host.JsonAsync(await _host.GetAsync("/v1/users/fan_one/lists", _owner), HttpStatusCode.OK);
        Assert.Equal(2, own.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetAsync("/v1/users/nobody/lists", _other)).StatusCode);
    }

    [Theory]
    [InlineData("""{"title":""}""", "title")]
    [InlineData("""{"title":"x","visibility":"friends"}""", "visibility")]
    public async Task Bad_list_fields_are_rejected(string body, string field)
    {
        var response = await _host.SendAsync(HttpMethod.Post, "/v1/lists", _owner, JsonDocument.Parse(body).RootElement);
        var problem = await _host.JsonAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(field, problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Bad_item_fields_are_rejected()
    {
        var listId = await CreateListAsync();
        var zero = await _host.JsonAsync(await _host.PostAsync($"/v1/lists/{listId}/items", _owner, new { album_id = _host.InRainbows, position = 0 }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("position", zero.GetProperty("errors")[0].GetProperty("field").GetString());
        var longNote = await _host.JsonAsync(
            await _host.PostAsync($"/v1/lists/{listId}/items", _owner, new { album_id = _host.InRainbows, note = new string('n', 501) }), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("note", longNote.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    private async Task<string> CreateListAsync() =>
        (await _host.JsonAsync(await _host.PostAsync("/v1/lists", _owner, new { title = "Favourites" }), HttpStatusCode.Created)).GetProperty("id").GetString()!;

    private async Task<JsonElement> Add(string listId, Guid albumId, string? note = null, int? position = null)
    {
        var body = new Dictionary<string, object> { ["album_id"] = albumId };
        if (note is not null) body["note"] = note;
        if (position is not null) body["position"] = position;
        return await _host.JsonAsync(await _host.PostAsync($"/v1/lists/{listId}/items", _owner, body), HttpStatusCode.Created);
    }

    private static IEnumerable<string?> Titles(JsonElement detail) =>
        detail.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("album").GetProperty("title").GetString());
}
