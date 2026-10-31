using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FandomScore.Api.Features.Authentication;
using FandomScore.Database;
using FandomScore.Tests.Contract;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FandomScore.Tests;

/// <summary>
/// The security pass's standing guards (2026-10-28): every /v1 route needs sign-in unless it's on a short reviewed list,
/// every /admin route needs staff, the contract says the same, and the running API answers 401 and 403 accordingly.
/// A new anonymous route fails here until it's added to <see cref="ReviewedAnonymous"/> with its reason.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SecurityRouteTests(PostgresFixture postgres)
{
    /// <summary>Routes that answer without sign-in, and why each is safe.</summary>
    private static readonly Dictionary<string, string> ReviewedAnonymous = new(StringComparer.Ordinal)
    {
        ["GET /health"] = "liveness probe; no data",
        ["POST /auth/sessions"] = "sign-in itself; rate-limited",
        ["POST /auth/refresh"] = "token refresh; rate-limited; needs a valid refresh token",
        ["POST /webhooks/apple"] = "Apple's server-to-server notifications; signature-checked JWT",
        ["PUT /uploads/{}/content"] = "upload bytes to a signed, expiring, single-use slot link",
        ["GET /files/{}"] = "a stored photo through a signed, expiring link",
        ["GET /avatars/{}"] = "a fan's profile picture by its unguessable upload id; answers only while it is their current picture",
        ["GET /featured/videos/{}"] = "a featured video through a signed, expiring link",
        ["GET /score-cards/{}/{}.png"] = "a score card image through a signed, expiring link",
    };

    private static List<(string Key, string Method, string Pattern, bool Anonymous, bool Staff)> Routes(WebApplicationFactory<Program> factory) =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/v1/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (
                Key: OpenApiContract.Key(m, e.RoutePattern.RawText!),
                Method: m,
                Pattern: e.RoutePattern.RawText!,
                Anonymous: e.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                Staff: e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == AuthSetup.StaffPolicy))))];

    [Fact]
    public void Only_reviewed_routes_skip_sign_in_admin_routes_need_staff_and_the_contract_agrees()
    {
        using var factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", "Host=localhost;Database=unused");
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
        });
        var routes = Routes(factory);
        Assert.NotEmpty(routes);

        var anonymous = routes.Where(r => r.Anonymous).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        Assert.True(anonymous.SetEquals(ReviewedAnonymous.Keys),
            "Anonymous routes changed. Review each and update ReviewedAnonymous:\n  new: " + string.Join(", ", anonymous.Except(ReviewedAnonymous.Keys))
            + "\n  gone: " + string.Join(", ", ReviewedAnonymous.Keys.Except(anonymous)));

        var adminWithoutStaff = routes.Where(r => r.Pattern.StartsWith("/v1/admin/", StringComparison.Ordinal) && !r.Staff).Select(r => r.Key).ToList();
        Assert.True(adminWithoutStaff.Count == 0, "Admin routes without the staff policy:\n  " + string.Join("\n  ", adminWithoutStaff));
        var staffOutsideAdmin = routes.Where(r => r.Staff && !r.Pattern.StartsWith("/v1/admin/", StringComparison.Ordinal)).Select(r => r.Key).ToList();
        Assert.True(staffOutsideAdmin.Count == 0, "Staff-only routes outside /admin (move them, or document why):\n  " + string.Join("\n  ", staffOutsideAdmin));

        // Planned operations (x-planned) aren't served yet; their security is checked when they're built.
        var contractAnonymous = OpenApiContract.Instance.Anonymous.Except(OpenApiContract.Instance.Planned).ToHashSet(StringComparer.Ordinal);
        Assert.True(contractAnonymous.SetEquals(anonymous),
            "openapi.yaml `security: []` doesn't match the anonymous routes:\n  only in the contract: " + string.Join(", ", contractAnonymous.Except(anonymous))
            + "\n  only in the API: " + string.Join(", ", anonymous.Except(contractAnonymous)));
    }

    [Fact]
    public async Task Every_protected_route_answers_401_without_sign_in_and_every_admin_route_403_to_a_fan()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"security_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        await using var factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IIdentityTokenValidator, FakeIdentityTokenValidator>()));
        });
        using var client = factory.CreateClient();
        var signIn = await client.PostAsJsonAsync("/v1/auth/sessions",
            new { provider = "google", nonce = TestAuth.Nonce, id_token = TestAuth.IdToken("g-security-fan", "security-fan@example.test"), handle = "security_fan" });
        var fan = JsonDocument.Parse(await signIn.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;

        var failures = new List<string>();
        var protectedRoutes = Routes(factory).Where(r => !r.Anonymous).ToList();
        Assert.True(protectedRoutes.Count > 100, $"Only {protectedRoutes.Count} protected routes found");
        Assert.True(protectedRoutes.Count(r => r.Staff) > 20, "Too few staff routes found");
        foreach (var route in protectedRoutes)
        {
            var path = Concrete(route.Pattern);
            using var anonymousCall = Request(route.Method, path, token: null);
            var status = (await client.SendAsync(anonymousCall)).StatusCode;
            if (status != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{route.Method} {path} without sign-in: {(int)status}, expected 401");
            }

            if (route.Staff)
            {
                using var fanCall = Request(route.Method, path, fan);
                var asFan = (await client.SendAsync(fanCall)).StatusCode;
                if (asFan != HttpStatusCode.Forbidden)
                {
                    failures.Add($"{route.Method} {path} as a fan: {(int)asFan}, expected 403");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static HttpRequestMessage Request(string method, string path, string? token)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    /// <summary>A real path for a route pattern: ids become new GUIDs, other parameters a plausible value.</summary>
    private static string Concrete(string pattern) =>
        System.Text.RegularExpressions.Regex.Replace(pattern, @"\{(?<name>[^}:]+)(:(?<constraint>[^}]+))?\}", m =>
            m.Groups["constraint"].Value.StartsWith("guid", StringComparison.Ordinal) ? Guid.NewGuid().ToString()
            : m.Groups["name"].Value switch
            {
                "day" => "2026-11-05",
                "source" => "ticketmaster",
                "targetType" => "photo",
                "reaction" => "fire",
                "position" => "1",
                _ => "someone",
            });
}
