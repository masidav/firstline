using FandomScore.Tests.Contract;
using System.Net;
using System.Text.Json;
using FandomScore.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FandomScore.Tests;

public sealed class ApiTests : IClassFixture<ApiTests.ApiFactory>
{
    private readonly HttpClient _client;

    public ApiTests(ApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await _client.GetAsync("/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_route_returns_not_found_problem()
    {
        var problem = await ReadProblemAsync(await _client.GetAsync("/v1/does-not-exist"), HttpStatusCode.NotFound);

        Assert.Equal("not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Api_problem_exception_keeps_its_status_and_code()
    {
        var problem = await ReadProblemAsync(await _client.GetAsync(ApiFactory.ProblemPath), HttpStatusCode.TooManyRequests);

        Assert.Equal("artist_add_limit", problem.GetProperty("code").GetString());
        Assert.Equal("Daily artist add limit reached", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unhandled_exception_returns_internal_error_without_details()
    {
        var response = await _client.GetAsync(ApiFactory.CrashPath);
        var problem = await ReadProblemAsync(response, HttpStatusCode.InternalServerError);

        Assert.Equal("internal_error", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(ApiFactory.CrashSecret, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    public sealed class ApiFactory : ContractCheckedFactory
    {
        public const string ProblemPath = "/__test/problem";
        public const string CrashPath = "/__test/crash";
        public const string CrashSecret = "connection password leaked";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Health and error handling never open a connection; no database is needed here.
            builder.UseSetting("ConnectionStrings:Database", "Host=localhost;Database=unused");
            builder.UseSetting("Auth:SigningKey", TestAuth.SigningKey);
            builder.UseEnvironment("Production");
            builder.UseSetting("App:PublicOrigin", TestAuth.PublicOrigin);
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, ThrowingRoutes>());
        }

        // Appended after the app's pipeline, so requests reach it only when no endpoint matched,
        // and its exceptions flow back through the app's exception handler.
        private sealed class ThrowingRoutes : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(
                Action<IApplicationBuilder> next) => app =>
            {
                next(app);
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Path == ProblemPath)
                    {
                        throw new ApiProblemException(429, "artist_add_limit", "Daily artist add limit reached");
                    }

                    if (context.Request.Path == CrashPath)
                    {
                        throw new InvalidOperationException(CrashSecret);
                    }

                    await nextMiddleware(context);
                });
            };
        }
    }
}
