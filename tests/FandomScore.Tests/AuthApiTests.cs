using FandomScore.Tests.Contract;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Sign-in, sessions, and /me against a real database (one per test).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _logs = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync($"auth_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(_connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());

        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Database", _connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Logging:LogLevel:Default", "Debug");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_logs)));
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task First_sign_in_creates_the_account_and_later_sign_ins_return_it()
    {
        var idToken = TestAuth.IdToken("g-1", "fan@example.test");

        var created = await SignInAsync(idToken, "fan_one", HttpStatusCode.Created);
        Assert.True(created.GetProperty("is_new_user").GetBoolean());
        Assert.Equal("Bearer", created.GetProperty("token_type").GetString());
        Assert.Equal(900, created.GetProperty("expires_in").GetInt32());
        var user = created.GetProperty("user");
        Assert.Equal("fan_one", user.GetProperty("handle").GetString());
        Assert.Equal("fan@example.test", user.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, user.GetProperty("display_name").ValueKind);
        Assert.False(user.GetProperty("is_staff").GetBoolean());

        var again = await SignInAsync(idToken, handle: null, HttpStatusCode.OK);
        Assert.False(again.GetProperty("is_new_user").GetBoolean());
        Assert.Equal(user.GetProperty("id").GetGuid(), again.GetProperty("user").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task New_accounts_need_a_valid_handle_and_a_verified_email()
    {
        var missing = await ProblemAsync(await PostSignInAsync(TestAuth.IdToken("g-1", "a@example.test"), null), 422);
        Assert.Equal("handle", missing.GetProperty("errors")[0].GetProperty("field").GetString());

        var invalid = await ProblemAsync(await PostSignInAsync(TestAuth.IdToken("g-1", "a@example.test"), "no spaces"), 422);
        Assert.Equal("handle", invalid.GetProperty("errors")[0].GetProperty("field").GetString());

        var unverified = await ProblemAsync(
            await PostSignInAsync(TestAuth.IdToken("g-1", "a@example.test", verified: false), "fan_one"), 422);
        Assert.Equal("validation_failed", unverified.GetProperty("code").GetString());

        var provider = await ProblemAsync(await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "myspace", id_token = "x", handle = "fan_one" }), 422);
        Assert.Equal("provider", provider.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Invalid_identity_token_is_unauthorized()
    {
        var problem = await ProblemAsync(await PostSignInAsync("forged", "fan_one"), 401);

        Assert.Equal("unauthorized", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Taken_handle_or_email_conflicts()
    {
        await SignInAsync(TestAuth.IdToken("g-1", "fan@example.test"), "fan_one", HttpStatusCode.Created);

        var handle = await ProblemAsync(await PostSignInAsync(TestAuth.IdToken("g-2", "other@example.test"), "FAN_ONE"), 409);
        Assert.Equal("handle_taken", handle.GetProperty("code").GetString());

        var email = await ProblemAsync(
            await PostSignInAsync(TestAuth.IdToken("a-1", "fan@example.test"), "fan_two", provider: "apple"), 409);
        Assert.Equal("email_in_use", email.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Me_requires_a_valid_current_access_token()
    {
        var session = await SignInAsync(TestAuth.IdToken("g-1", "fan@example.test"), "fan_one", HttpStatusCode.Created);
        var accessToken = session.GetProperty("access_token").GetString()!;

        var anonymous = await ProblemAsync(await _client.GetAsync("/v1/me"), 401);
        Assert.Equal("unauthorized", anonymous.GetProperty("code").GetString());
        await ProblemAsync(await GetMeAsync("not-a-jwt"), 401);

        // Correct claims, but expired or signed with another key.
        var claims = new JsonWebTokenHandler().ReadJsonWebToken(accessToken);
        await ProblemAsync(await GetMeAsync(ForgeAccessToken(claims, TestAuth.SigningKey, DateTime.UtcNow.AddMinutes(-5))), 401);
        await ProblemAsync(await GetMeAsync(ForgeAccessToken(
            claims, Convert.ToBase64String(new byte[32]), DateTime.UtcNow.AddMinutes(5))), 401);

        var me = await JsonAsync(await GetMeAsync(accessToken), HttpStatusCode.OK);
        Assert.Equal("fan_one", me.GetProperty("handle").GetString());
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_reuse_revokes_the_session()
    {
        var session = await SignInAsync(TestAuth.IdToken("g-1", "fan@example.test"), "fan_one", HttpStatusCode.Created);
        var firstRefresh = session.GetProperty("refresh_token").GetString()!;

        var rotated = await JsonAsync(await RefreshAsync(firstRefresh), HttpStatusCode.OK);
        var secondRefresh = rotated.GetProperty("refresh_token").GetString()!;
        var accessToken = rotated.GetProperty("access_token").GetString()!;
        Assert.NotEqual(firstRefresh, secondRefresh);
        Assert.False(rotated.GetProperty("is_new_user").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(accessToken)).StatusCode);

        // Someone replays the old token: the whole session ends, for them and for the real device.
        await ProblemAsync(await RefreshAsync(firstRefresh), 401);
        await ProblemAsync(await RefreshAsync(secondRefresh), 401);
        await ProblemAsync(await GetMeAsync(accessToken), 401);

        await ProblemAsync(await RefreshAsync("never-issued"), 401);
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        var session = await SignInAsync(TestAuth.IdToken("g-1", "fan@example.test"), "fan_one", HttpStatusCode.Created);
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE refresh_tokens SET issued_at = now() - interval '31 days', expires_at = now() - interval '1 day'",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        await ProblemAsync(await RefreshAsync(session.GetProperty("refresh_token").GetString()!), 401);
    }

    [Fact]
    public async Task Sign_out_ends_only_the_current_session()
    {
        var idToken = TestAuth.IdToken("g-1", "fan@example.test");
        var phone = await SignInAsync(idToken, "fan_one", HttpStatusCode.Created);
        var tablet = await SignInAsync(idToken, null, HttpStatusCode.OK);
        var phoneAccess = phone.GetProperty("access_token").GetString()!;

        using var signOut = new HttpRequestMessage(HttpMethod.Delete, "/v1/auth/sessions/current");
        signOut.Headers.Authorization = new AuthenticationHeaderValue("Bearer", phoneAccess);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(signOut)).StatusCode);

        await ProblemAsync(await GetMeAsync(phoneAccess), 401);
        await ProblemAsync(await RefreshAsync(phone.GetProperty("refresh_token").GetString()!), 401);
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(tablet.GetProperty("access_token").GetString()!)).StatusCode);
    }

    [Fact]
    public async Task Profile_updates_are_validated()
    {
        await SignInAsync(TestAuth.IdToken("g-2", "taken@example.test"), "taken", HttpStatusCode.Created);
        var session = await SignInAsync(TestAuth.IdToken("g-1", "fan@example.test"), "fan_one", HttpStatusCode.Created);
        var token = session.GetProperty("access_token").GetString()!;

        var updated = await JsonAsync(await PatchMeAsync(token, new { handle = "fan_renamed", display_name = "Fan 🎸" }), HttpStatusCode.OK);
        Assert.Equal("fan_renamed", updated.GetProperty("handle").GetString());
        Assert.Equal("Fan 🎸", updated.GetProperty("display_name").GetString());

        var cleared = await JsonAsync(await PatchMeAsync(token, new { display_name = (string?)null }), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("display_name").ValueKind);
        Assert.Equal("fan_renamed", cleared.GetProperty("handle").GetString());

        var invalid = await ProblemAsync(await PatchMeAsync(token, new { handle = "x", display_name = new string('a', 61) }), 422);
        Assert.Equal(2, invalid.GetProperty("errors").GetArrayLength());

        var avatar = await ProblemAsync(await PatchMeAsync(token, new { avatar_upload_id = Guid.NewGuid() }), 422);
        Assert.Equal("avatar_upload_id", avatar.GetProperty("errors")[0].GetProperty("field").GetString());

        var taken = await ProblemAsync(await PatchMeAsync(token, new { handle = "Taken" }), 409);
        Assert.Equal("handle_taken", taken.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Artist_profile_links_need_the_artist_flag()
    {
        var session = await SignInAsync(TestAuth.IdToken("g-1", "fan@example.test"), "fan_one", HttpStatusCode.Created);
        var token = session.GetProperty("access_token").GetString()!;
        Assert.False(session.GetProperty("user").GetProperty("is_artist").GetBoolean());

        // Links without the flag: the database constraint answers as a validation error.
        var early = await ProblemAsync(await PatchMeAsync(token, new { band_url = "https://example.test/band" }), 422);
        Assert.Equal("is_artist", early.GetProperty("errors")[0].GetProperty("field").GetString());

        var artist = await JsonAsync(await PatchMeAsync(token, new
        {
            is_artist = true, band_url = "https://open.spotify.com/artist/abc", instagram_handle = "@the.band", x_handle = "theband",
        }), HttpStatusCode.OK);
        Assert.True(artist.GetProperty("is_artist").GetBoolean());
        Assert.Equal("https://open.spotify.com/artist/abc", artist.GetProperty("band_url").GetString());
        Assert.Equal("the.band", artist.GetProperty("instagram_handle").GetString()); // leading @ dropped
        Assert.Equal("theband", artist.GetProperty("x_handle").GetString());

        var invalid = await ProblemAsync(await PatchMeAsync(token, new
        {
            band_url = "http://example.test", instagram_handle = "no spaces", x_handle = new string('x', 16),
        }), 422);
        Assert.Equal(3, invalid.GetProperty("errors").GetArrayLength());

        var contradiction = await ProblemAsync(await PatchMeAsync(token, new { is_artist = false, x_handle = "theband" }), 422);
        Assert.Equal("x_handle", contradiction.GetProperty("errors")[0].GetProperty("field").GetString());
        var doubleAt = await ProblemAsync(await PatchMeAsync(token, new { x_handle = "@@theband" }), 422);
        Assert.Equal("x_handle", doubleAt.GetProperty("errors")[0].GetProperty("field").GetString());

        var cleared = await JsonAsync(await PatchMeAsync(token, new { x_handle = (string?)null }), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("x_handle").ValueKind);
        Assert.Equal("the.band", cleared.GetProperty("instagram_handle").GetString());

        // Leaving the artist role drops the links (also when the client clears one explicitly in the same request).
        var fan = await JsonAsync(await PatchMeAsync(token, new { is_artist = false, band_url = (string?)null }), HttpStatusCode.OK);
        Assert.False(fan.GetProperty("is_artist").GetBoolean());
        Assert.Equal(JsonValueKind.Null, fan.GetProperty("band_url").ValueKind);
        Assert.Equal(JsonValueKind.Null, fan.GetProperty("instagram_handle").ValueKind);
    }

    [Fact]
    public async Task Deleting_the_account_ends_access_and_frees_the_identity()
    {
        var idToken = TestAuth.IdToken("g-1", "fan@example.test");
        var session = await SignInAsync(idToken, "fan_one", HttpStatusCode.Created);
        var token = session.GetProperty("access_token").GetString()!;
        var oldId = session.GetProperty("user").GetProperty("id").GetGuid();

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/v1/me");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Accepted, (await _client.SendAsync(delete)).StatusCode);

        await ProblemAsync(await GetMeAsync(token), 401);
        await ProblemAsync(await RefreshAsync(session.GetProperty("refresh_token").GetString()!), 401);

        // The same Google account starts over as a new user, and may reuse the freed handle and email.
        await ProblemAsync(await PostSignInAsync(idToken, null), 422);
        var fresh = await SignInAsync(idToken, "fan_one", HttpStatusCode.Created);
        Assert.NotEqual(oldId, fresh.GetProperty("user").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Credentials_are_never_logged()
    {
        var idToken = TestAuth.IdToken("g-secret-subject", "fan@example.test");
        var session = await SignInAsync(idToken, "fan_one", HttpStatusCode.Created);
        var rotated = await JsonAsync(await RefreshAsync(session.GetProperty("refresh_token").GetString()!), HttpStatusCode.OK);
        await GetMeAsync(rotated.GetProperty("access_token").GetString()!);
        await RefreshAsync(session.GetProperty("refresh_token").GetString()!); // reuse, logs a failure path

        Assert.NotEmpty(_logs);
        string[] secrets =
        [
            idToken,
            session.GetProperty("refresh_token").GetString()!,
            session.GetProperty("access_token").GetString()!,
            rotated.GetProperty("refresh_token").GetString()!,
            rotated.GetProperty("access_token").GetString()!,
        ];
        foreach (var message in _logs)
        {
            Assert.DoesNotContain(secrets, message.Contains);
        }
    }

    private Task<HttpResponseMessage> PostSignInAsync(string idToken, string? handle, string provider = "google") =>
        _client.PostAsJsonAsync("/v1/auth/sessions", new { provider, id_token = idToken, handle });

    private async Task<JsonElement> SignInAsync(string idToken, string? handle, HttpStatusCode expected) =>
        await JsonAsync(await PostSignInAsync(idToken, handle), expected);

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _client.PostAsJsonAsync("/v1/auth/refresh", new { refresh_token = refreshToken });

    private Task<HttpResponseMessage> GetMeAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PatchMeAsync(string accessToken, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, "/v1/me") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return _client.SendAsync(request);
    }

    private static string ForgeAccessToken(JsonWebToken real, string base64Key, DateTime expires) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = real.Issuer,
            Audience = real.Audiences.Single(),
            IssuedAt = expires.AddMinutes(-15),
            NotBefore = expires.AddMinutes(-15),
            Expires = expires,
            Claims = new Dictionary<string, object> { ["sub"] = real.Subject, ["sid"] = real.GetClaim("sid").Value },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(base64Key)), SecurityAlgorithms.HmacSha256),
        });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, int expected)
    {
        var problem = await JsonAsync(response, (HttpStatusCode)expected);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return problem;
    }
}
