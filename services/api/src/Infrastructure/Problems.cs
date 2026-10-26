using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FandomScore.Api.Infrastructure;

/// <summary>
/// An expected failure, rendered as RFC 9457 problem+json. <paramref name="code"/> must be one of
/// the stable codes in openapi.yaml (components.schemas.Problem.code).
/// </summary>
public sealed class ApiProblemException(
    int status, string code, string title, string? detail = null, IReadOnlyList<FieldError>? errors = null)
    : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public IReadOnlyList<FieldError>? Errors { get; } = errors;

    /// <summary>Sent as the <c>Retry-After</c> header (with 429 and 503).</summary>
    public TimeSpan? RetryAfter { get; init; }

    public static ApiProblemException Unauthorized(string detail) =>
        new(StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized", detail);

    public static ApiProblemException Validation(params FieldError[] errors) =>
        new(StatusCodes.Status422UnprocessableEntity, "validation_failed", "Request is invalid", errors: errors);
}

/// <summary>One invalid request field (Problem.errors in openapi.yaml).</summary>
public sealed record FieldError(string Field, string Message);

public static class Problems
{
    /// <summary>Every error response is problem+json and carries a <c>code</c>.</summary>
    public static IServiceCollection AddApiProblems(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            // RFC 9457: a problem without a more specific type is "about:blank". ASP.NET fills in a type for some
            // statuses only (not 429, for one); openapi.yaml requires it on every problem.
            problem.Type ??= "about:blank";
            problem.Extensions.TryAdd("code", CodeForStatus(problem.Status ?? StatusCodes.Status500InternalServerError));
        });
        services.AddExceptionHandler<ApiProblemExceptionHandler>();
        return services;
    }

    // Fallback for responses not raised as ApiProblemException (unmatched routes, unhandled errors).
    private static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "unauthorized",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status405MethodNotAllowed => "not_found",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status422UnprocessableEntity => "validation_failed",
        StatusCodes.Status429TooManyRequests => "rate_limited",
        < 500 => "validation_failed",
        _ => "internal_error",
    };

    private sealed class ApiProblemExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not ApiProblemException problem)
            {
                return false;
            }

            http.Response.StatusCode = problem.Status;
            if (problem.RetryAfter is { } retryAfter)
            {
                http.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(Math.Max(retryAfter.TotalSeconds, 1))).ToString(CultureInfo.InvariantCulture);
            }

            var details = new ProblemDetails
            {
                Status = problem.Status,
                Title = problem.Title,
                Detail = problem.Detail,
                Extensions = { ["code"] = problem.Code },
            };
            if (problem.Errors is { Count: > 0 } errors)
            {
                details.Extensions["errors"] = errors;
            }

            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                Exception = exception,
                ProblemDetails = details,
            });
        }
    }
}
