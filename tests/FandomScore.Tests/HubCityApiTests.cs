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

/// <summary>GET /cities, PUT /me/hub-cities, and hub cities on Me, against a real database (migration 0010).</summary>
[Collection(PostgresCollection.Name)]
public sealed class HubCityApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"hub_cities_{Guid.NewGuid():N}");
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
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task A_new_account_has_no_hub_cities_yet()
    {
        var token = await SignInAsync("fan_one");

        var me = await JsonAsync(await SendAsync(HttpMethod.Get, "/v1/me", token), HttpStatusCode.OK);

        Assert.Empty(me.GetProperty("hub_cities").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("hub_cities_change_available_at").ValueKind);
    }

    [Theory]
    [InlineData("q=york", "New York")]
    [InlineData("q=SAN%20F", "San Francisco")]
    [InlineData("q=on&limit=200", "Toronto")]
    [InlineData("q=lake", "Salt Lake City")]
    public async Task Cities_are_searched_by_name_word_or_region(string query, string expected)
    {
        var token = await SignInAsync("fan_one");

        var cities = await JsonAsync(await SendAsync(HttpMethod.Get, $"/v1/cities?{query}", token), HttpStatusCode.OK);

        Assert.Contains(cities.GetProperty("items").EnumerateArray(), c => c.GetProperty("name").GetString() == expected);
    }

    [Fact]
    public async Task Cities_need_sign_in()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/v1/cities")).StatusCode);
    }

    [Fact]
    public async Task Choosing_then_changing_hub_cities_is_limited_to_once_a_week()
    {
        var token = await SignInAsync("fan_one");

        var first = await JsonAsync(await PutAsync(token, "us-il-chicago", "ca-on-toronto"), HttpStatusCode.OK);
        Assert.Equal(["us-il-chicago", "ca-on-toronto"], Ids(first));
        Assert.Equal("Chicago", first.GetProperty("hub_cities")[0].GetProperty("name").GetString());
        Assert.Equal("IL", first.GetProperty("hub_cities")[0].GetProperty("region").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("hub_cities_change_available_at").ValueKind);

        // The first choice doesn't count, so one change is allowed right away; it starts the weekly clock.
        var changed = await JsonAsync(await PutAsync(token, "ca-on-toronto", "us-il-chicago", "us-ny-new-york"), HttpStatusCode.OK);
        Assert.Equal(["ca-on-toronto", "us-il-chicago", "us-ny-new-york"], Ids(changed));
        var nextChange = changed.GetProperty("hub_cities_change_available_at").GetDateTime();
        Assert.InRange(nextChange, DateTime.UtcNow.AddDays(7).AddMinutes(-5), DateTime.UtcNow.AddDays(7).AddMinutes(5));

        // The same list again is fine; a different one is refused until next week.
        await JsonAsync(await PutAsync(token, "ca-on-toronto", "us-il-chicago", "us-ny-new-york"), HttpStatusCode.OK);
        var response = await PutAsync(token, "us-il-chicago");
        var problem = await JsonAsync(response, HttpStatusCode.TooManyRequests);
        Assert.Equal("hub_change_limit", problem.GetProperty("code").GetString());
        var retryAfter = int.Parse(response.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 7 * 24 * 3600 - 600, 7 * 24 * 3600);

        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE users SET hubs_changed_at = now() - interval '8 days' WHERE handle = 'fan_one'");
        }

        Assert.Equal(["us-il-chicago"], Ids(await JsonAsync(await PutAsync(token, "us-il-chicago"), HttpStatusCode.OK)));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"city_ids": []}""")]
    [InlineData("""{"city_ids": ["us-il-chicago", "ca-on-toronto", "us-ny-new-york", "us-tx-austin"]}""")]
    [InlineData("""{"city_ids": ["us-il-chicago", "us-il-chicago"]}""")]
    [InlineData("""{"city_ids": ["us-xx-atlantis"]}""")]
    [InlineData("""{"city_ids": [42]}""")]
    public async Task Hub_city_lists_are_validated(string body)
    {
        var token = await SignInAsync("fan_one");

        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/me/hub-cities")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var problem = await JsonAsync(await _client.SendAsync(request), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("city_ids", problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    private static List<string?> Ids(JsonElement me) =>
        [.. me.GetProperty("hub_cities").EnumerateArray().Select(c => c.GetProperty("id").GetString())];

    private Task<HttpResponseMessage> PutAsync(string token, params string[] cityIds) =>
        SendAsync(HttpMethod.Put, "/v1/me/hub-cities", token, JsonContent.Create(new { city_ids = cityIds }));

    private async Task<string> SignInAsync(string handle)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken($"g-{handle}", $"{handle}@example.test"), handle });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }
}
