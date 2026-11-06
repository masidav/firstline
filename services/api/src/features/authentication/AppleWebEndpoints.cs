using System.Text;
using System.Text.Json;
using FandomScore.Api.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace FandomScore.Api.Features.Authentication;

/// <summary>
/// Sign in with Apple on the web (openapi appleWebCallback). Apple only returns an identity token with the email scope
/// through a <c>form_post</c> to an https page, so the browser can't receive it in a URL fragment the way it does from
/// Google. The web app sends the fan to Apple with this endpoint as the redirect URI; Apple posts the token here; this
/// endpoint hands it straight back to the app in a fragment, where the app finishes with <c>POST /auth/sessions</c>
/// (nonce check, account lookup) exactly as the native app does. Nothing is validated, stored, or logged here: the
/// token is opaque to this endpoint, and the fragment never reaches a server.
///
/// The app's origin travels inside <c>state</c> (base64url JSON <c>{"o": origin, "s": random}</c>) and must be one of
/// <c>Cors:AllowedOrigins</c>, so this can't be turned into an open redirect; the app compares the whole state value
/// with what it stored before leaving, as it does for Google.
/// </summary>
public static class AppleWebEndpoints
{
    public const string CallbackPath = "/auth/apple/callback";

    /// <summary>The app route Apple's answer is handed to, under the app's own origin.</summary>
    public const string AppRoute = "/apple-callback";

    private const int MaxFieldLength = 8192;

    public static void MapAppleWeb(this RouteGroupBuilder v1) =>
        v1.MapPost(CallbackPath, RelayAsync).AllowAnonymous().RequireRateLimiting(AuthSetup.SignInRateLimit);

    private static async Task<IResult> RelayAsync(HttpContext http, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!http.Request.HasFormContentType)
        {
            throw ApiProblemException.Validation(new FieldError("state", "Apple posts a form here."));
        }

        var form = await http.Request.ReadFormAsync(cancellationToken);
        var state = Field(form, "state");
        var origin = OriginFromState(state, configuration)
            ?? throw ApiProblemException.Validation(new FieldError("state", "The sign-in didn't start from an allowed app origin."));

        // Apple sends id_token (and code, and on the first sign-in a user JSON we don't need: the token carries the
        // email), or error (user_cancelled_authorize when the fan backs out).
        var fragment = new Dictionary<string, string?> { ["state"] = state };
        var idToken = Field(form, "id_token");
        var error = Field(form, "error");
        if (idToken.Length > 0)
        {
            fragment["id_token"] = idToken;
        }
        else
        {
            fragment["error"] = error.Length > 0 ? error : "no_token";
        }

        // 303: the browser follows with GET whatever Apple posted with. The fragment stays in the browser.
        var location = QueryHelpers.AddQueryString(origin + AppRoute, fragment).Replace('?', '#');
        http.Response.Headers.Location = location;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static string Field(IFormCollection form, string name) =>
        form.TryGetValue(name, out StringValues value) && value.Count > 0 && value[0] is { Length: > 0 and <= MaxFieldLength } text
            ? text
            : "";

    /// <summary>The origin named in <paramref name="state"/>, when it is one the API serves browsers from; else null.</summary>
    public static string? OriginFromState(string state, IConfiguration configuration)
    {
        if (state.Length is 0 or > 1024)
        {
            return null;
        }

        string? origin;
        try
        {
            using var document = JsonDocument.Parse(WebEncoders.Base64UrlDecode(state));
            origin = document.RootElement.ValueKind == JsonValueKind.Object
                     && document.RootElement.TryGetProperty("o", out var o) && o.ValueKind == JsonValueKind.String
                ? o.GetString()
                : null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }

        if (origin is null || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.PathAndQuery != "/" || uri.Fragment.Length > 0)
        {
            return null;
        }

        var allowed = (configuration["Cors:AllowedOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Contains(origin, StringComparer.Ordinal) ? origin : null;
    }

    /// <summary>What the web app puts in <c>state</c> (here for tests and documentation).</summary>
    public static string State(string origin, string random) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { o = origin, s = random })));
}
