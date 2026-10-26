using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FandomScore.Tests.Contract;

/// <summary>
/// The API's routes and openapi.yaml list the same operations: nothing the API serves is missing from the contract,
/// and nothing in the contract is unbuilt without saying so (<c>x-planned</c> or <c>x-requires-schema</c>).
/// Every response the other tests get is checked against the contract by <see cref="ContractCheckedFactory"/>.
/// </summary>
public sealed class ContractCoverageTests
{
    [Fact]
    public void Every_route_the_api_serves_is_in_openapi_yaml_and_the_other_way_round()
    {
        using var factory = new ContractCheckedFactory().WithWebHostBuilder(builder =>
        {
            // Listing routes opens no connection.
            builder.UseSetting("ConnectionStrings:Database", "Host=localhost;Database=unused");
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
        });
        var served = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/v1/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => OpenApiContract.Key(m, e.RoutePattern.RawText!)))
            .ToHashSet(StringComparer.Ordinal);
        var documented = OpenApiContract.Instance.Operations.ToHashSet(StringComparer.Ordinal);

        var undocumented = served.Except(documented).Order(StringComparer.Ordinal).ToList();
        Assert.True(undocumented.Count == 0, "Served but not in openapi.yaml:\n  " + string.Join("\n  ", undocumented));

        var unbuilt = documented.Except(served).Except(OpenApiContract.Instance.Planned).Order(StringComparer.Ordinal).ToList();
        Assert.True(unbuilt.Count == 0, "In openapi.yaml but not served (build it, or mark it x-planned):\n  " + string.Join("\n  ", unbuilt));

        var stale = served.Intersect(OpenApiContract.Instance.Planned).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "Served but still marked x-planned or x-requires-schema in openapi.yaml:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void A_response_that_breaks_the_contract_is_caught()
    {
        var contract = OpenApiContract.Instance;
        var city = """{"items":[{"id":"us-il-chicago","name":"Chicago","region":"IL","country":"US"}]}"""u8;

        Assert.Contains(contract.Check("GET", "/v1/cities", 200, "application/json", city), v => v.Contains("time_zone", StringComparison.Ordinal));
        Assert.Contains(contract.Check("GET", "/v1/cities", 418, "application/json", city), v => v.Contains("418", StringComparison.Ordinal));
        Assert.Contains(contract.Check("GET", "/v1/no-such-thing", 200, null, []), v => v.Contains("not in openapi.yaml", StringComparison.Ordinal));
        // The conventions: a signed-in operation may answer 401, an /admin one 403, with a problem body.
        var problem = """{"type":"about:blank","title":"Unauthorized","status":401,"code":"unauthorized"}"""u8;
        Assert.Empty(contract.Check("GET", "/v1/cities", 401, "application/problem+json", problem));
        Assert.NotEmpty(contract.Check("GET", "/v1/cities", 401, "application/problem+json", """{"title":"no code"}"""u8));
    }
}
