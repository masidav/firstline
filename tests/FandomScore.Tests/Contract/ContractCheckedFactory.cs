using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FandomScore.Tests.Contract;

/// <summary>
/// The API for tests, with every <c>/v1</c> response checked against openapi.yaml: the status must be documented for
/// the operation, and a JSON body must match its schema. A mismatch fails the request (and so the test) with what
/// differs. With <c>CONTRACT_CHECK=report</c>, mismatches are appended to <c>CONTRACT_REPORT</c> instead, to survey a
/// whole run.
/// </summary>
public class ContractCheckedFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, ContractCheck>());

    private sealed class ContractCheck : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(CheckAsync);
            next(app);
        };
    }

    private static readonly ConcurrentDictionary<string, byte> Reported = new();

    private static async Task CheckAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/v1", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        // Only requests that reached an endpoint are operations; a 404 from routing has no operation to check.
        if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route })
        {
            var violations = OpenApiContract.Instance.Check(context.Request.Method, route, context.Response.StatusCode,
                context.Response.ContentType, buffer.ToArray());
            if (violations.Count > 0)
            {
                if (Environment.GetEnvironmentVariable("CONTRACT_CHECK") == "report")
                {
                    var report = Environment.GetEnvironmentVariable("CONTRACT_REPORT") ?? Path.Combine(Path.GetTempPath(), "contract-report.txt");
                    var fresh = violations.Where(v => Reported.TryAdd(v, 0)).ToList();
                    if (fresh.Count > 0)
                    {
                        lock (Reported)
                        {
                            File.AppendAllLines(report, fresh);
                        }
                    }
                }
                else
                {
                    throw new ContractViolationException(
                        $"{context.Request.Method} {context.Request.Path} doesn't match openapi.yaml:\n  " + string.Join("\n  ", violations));
                }
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original);
    }
}

public sealed class ContractViolationException(string message) : Exception(message);
