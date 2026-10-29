using FandomScore.Tests.Contract;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FandomScore.Tests;

/// <summary>
/// Sign in with Apple end to end: real token validation (with a local key standing in for Apple's), account
/// deletion revoking the Apple sign-in, and Apple's server-to-server notifications.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AppleTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private const string AppleIssuer = "https://appleid.apple.com";
    private const string BundleId = "com.example.fandom";
    private const string TeamId = "TEAM123456";
    private const string KeyId = "KEY1234567";

    private readonly RSA _appleSigningKey = RSA.Create(2048);
    private readonly ECDsa _appKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeAppleApi _appleApi = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"apple_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());

        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(new RsaSecurityKey(_appleSigningKey) { KeyId = "apple-1" });
        var validator = new OidcIdentityTokenValidator(new Dictionary<string, OidcProvider>
        {
            ["apple"] = new([AppleIssuer], [BundleId], new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration)),
        });

        _factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("App:PublicOrigin", TestAuth.PublicOrigin);
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseSetting("Auth:Apple:ClientIds", $"{BundleId},com.example.web");
            builder.UseSetting("Auth:Apple:TeamId", TeamId);
            builder.UseSetting("Auth:Apple:KeyId", KeyId);
            builder.UseSetting("Auth:Apple:PrivateKey", _appKey.ExportPkcs8PrivateKeyPem());
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator>(validator));
                services.AddHttpClient<AppleClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => _appleApi)
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            });
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose()
    {
        _appleSigningKey.Dispose();
        _appKey.Dispose();
        _appleApi.Dispose();
    }

    [Fact]
    public async Task Real_apple_identity_token_signs_in()
    {
        var created = await SignInAsync("a-1", "fan_one", HttpStatusCode.Created);
        Assert.Equal("relay@privaterelay.appleid.com", created.GetProperty("user").GetProperty("email").GetString());

        var forged = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "apple", nonce = TestAuth.Nonce, id_token = AppleJwt(new() { ["sub"] = "a-1" }, audience: "someone-else") });
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task Apple_sign_in_needs_the_nonce_the_token_carries()
    {
        var missing = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "apple", id_token = IdToken("a-1"), handle = "fan_one" });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var empty = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "apple", nonce = "", id_token = IdToken("a-1"), handle = "fan_one" });
        Assert.Equal(HttpStatusCode.Unauthorized, empty.StatusCode);

        var other = await _client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "apple", nonce = "another-sign-in", id_token = IdToken("a-1"), handle = "fan_one" });
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);

        // A token without any nonce (issued for some other flow) can't be used to sign in either.
        var unbound = await _client.PostAsJsonAsync("/v1/auth/sessions", new
        {
            provider = "apple",
            nonce = TestAuth.Nonce,
            id_token = AppleJwt(new() { ["sub"] = "a-1", ["email"] = "relay@privaterelay.appleid.com", ["email_verified"] = "true" }),
            handle = "fan_one",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unbound.StatusCode);

        await SignInAsync("a-1", "fan_one", HttpStatusCode.Created);
    }

    [Fact]
    public async Task Deleting_an_apple_account_revokes_the_apple_sign_in()
    {
        var session = await SignInAsync("a-1", "fan_one", HttpStatusCode.Created);
        _appleApi.TokenResponse = new { refresh_token = "apple-refresh", id_token = IdToken("a-1") };

        Assert.Equal(HttpStatusCode.Accepted, (await DeleteMeAsync(session, "code-from-app")).StatusCode);

        Assert.Equal(["auth/token", "auth/revoke"], _appleApi.Calls.Select(c => c.Path));
        var exchange = _appleApi.Calls.First().Form;
        Assert.Equal("code-from-app", exchange["code"]);
        Assert.Equal(BundleId, exchange["client_id"]);
        var revoke = _appleApi.Calls.Last().Form;
        Assert.Equal("apple-refresh", revoke["token"]);
        Assert.Equal("refresh_token", revoke["token_type_hint"]);

        // The client secret is an ES256 JWT from our team, signed with the Sign in with Apple key.
        var secret = await new JsonWebTokenHandler().ValidateTokenAsync(revoke["client_secret"], new TokenValidationParameters
        {
            ValidIssuer = TeamId,
            ValidAudience = AppleIssuer,
            IssuerSigningKey = new ECDsaSecurityKey(_appKey),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });
        Assert.True(secret.IsValid, secret.Exception?.Message);
        Assert.Equal(BundleId, secret.Claims["sub"]);
        Assert.Equal(KeyId, ((JsonWebToken)secret.SecurityToken).Kid);
    }

    [Fact]
    public async Task Code_for_another_apple_account_is_not_used_and_deletion_still_happens()
    {
        var session = await SignInAsync("a-1", "fan_one", HttpStatusCode.Created);
        _appleApi.TokenResponse = new { refresh_token = "someone-elses", id_token = IdToken("a-2") };

        Assert.Equal(HttpStatusCode.Accepted, (await DeleteMeAsync(session, "other-code")).StatusCode);

        Assert.Equal(["auth/token"], _appleApi.Calls.Select(c => c.Path));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(session)).StatusCode);
    }

    [Fact]
    public async Task Deletion_without_a_code_still_deletes()
    {
        var session = await SignInAsync("a-1", "fan_one", HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.Accepted, (await DeleteMeAsync(session, code: null)).StatusCode);

        Assert.Empty(_appleApi.Calls);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(session)).StatusCode);
    }

    [Fact]
    public async Task Consent_revoked_signs_the_user_out_everywhere_but_keeps_the_account()
    {
        var phone = await SignInAsync("a-1", "fan_one", HttpStatusCode.Created);
        var tablet = await SignInAsync("a-1", null, HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(AppleEvent("consent-revoked", "a-1"))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(phone)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(tablet)).StatusCode);
        var back = await SignInAsync("a-1", null, HttpStatusCode.OK);
        Assert.Equal(phone.GetProperty("user").GetProperty("id").GetGuid(), back.GetProperty("user").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Apple_account_deleted_deletes_the_account()
    {
        var session = await SignInAsync("a-1", "fan_one", HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(AppleEvent("account-delete", "a-1"))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(session)).StatusCode);
        // The handle and email are free again.
        await SignInAsync("a-9", "fan_one", HttpStatusCode.Created);
    }

    [Fact]
    public async Task Other_events_and_unknown_users_are_accepted_without_changes()
    {
        var session = await SignInAsync("a-1", "fan_one", HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(AppleEvent("email-disabled", "a-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(AppleEvent("consent-revoked", "nobody"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(session)).StatusCode);
    }

    [Fact]
    public async Task Forged_notifications_are_rejected()
    {
        var session = await SignInAsync("a-1", "fan_one", HttpStatusCode.Created);
        using var otherKey = RSA.Create(2048);
        var forged = AppleJwt(EventClaims("account-delete", "a-1"), signingKey: otherKey);

        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(forged)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync("not-a-jwt")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(AppleJwt(new() { ["sub"] = "a-1" }))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(session)).StatusCode);
    }

    private string IdToken(string subject) => AppleJwt(new()
    {
        ["sub"] = subject,
        ["email"] = "relay@privaterelay.appleid.com",
        ["email_verified"] = "true",
        ["is_private_email"] = "true",
        ["nonce"] = TestAuth.HashedNonce,
    });

    private string AppleEvent(string type, string subject) => AppleJwt(EventClaims(type, subject));

    private static Dictionary<string, object> EventClaims(string type, string subject) => new()
    {
        ["jti"] = Guid.NewGuid().ToString(),
        ["events"] = JsonSerializer.Serialize(new { type, sub = subject, event_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }),
    };

    private string AppleJwt(Dictionary<string, object> claims, string audience = BundleId, RSA? signingKey = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AppleIssuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(signingKey ?? _appleSigningKey) { KeyId = "apple-1" }, SecurityAlgorithms.RsaSha256),
        });

    private async Task<JsonElement> SignInAsync(string subject, string? handle, HttpStatusCode expected)
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/sessions", new { provider = "apple", nonce = TestAuth.Nonce, id_token = IdToken(subject), handle });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private Task<HttpResponseMessage> NotifyAsync(string payload) =>
        _client.PostAsJsonAsync("/v1/webhooks/apple", new { payload });

    private Task<HttpResponseMessage> GetMeAsync(JsonElement session) => SendAsync(HttpMethod.Get, session, null);

    private Task<HttpResponseMessage> DeleteMeAsync(JsonElement session, string? code) =>
        SendAsync(HttpMethod.Delete, session, code is null ? null : JsonContent.Create(new { apple_authorization_code = code }));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, JsonElement session, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, "/v1/me") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("access_token").GetString());
        return _client.SendAsync(request);
    }

    /// <summary>Stands in for appleid.apple.com and records what the API sent.</summary>
    private sealed class FakeAppleApi : HttpMessageHandler
    {
        public ConcurrentQueue<(string Path, Dictionary<string, string> Form)> Calls { get; } = new();

        public object TokenResponse { get; set; } = new { };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                .Select(pair => pair.Split('='))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Calls.Enqueue((path, form));
            return path == "auth/token"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(TokenResponse) }
                : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
