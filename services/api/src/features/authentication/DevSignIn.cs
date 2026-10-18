using FandomScore.Api.Features.Users;
using Microsoft.Extensions.Options;

namespace FandomScore.Api.Features.Authentication;

/// <summary>
/// Local developer sign-in, so the app can be tried without Apple or Google accounts. With
/// <c>Auth:DevSignIn=true</c>, a Google sign-in whose identity token is <c>dev:&lt;name&gt;</c> is accepted
/// without checking it, as the Google account <c>dev:&lt;name&gt;</c> with email <c>&lt;name&gt;@example.test</c>.
/// Anyone who can reach the API can then sign in as anyone, so the API refuses to start with it outside
/// the Development environment (<see cref="DevSignInGuard"/>).
/// </summary>
public sealed class DevSignInValidator(IIdentityTokenValidator inner) : IIdentityTokenValidator
{
    public const string TokenPrefix = "dev:";

    public bool IsEnabled(string provider) => provider == "google" || inner.IsEnabled(provider);

    public Task<ProviderIdentity?> ValidateAsync(string provider, string idToken, string? nonce, CancellationToken cancellationToken)
    {
        if (provider != "google" || !idToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return inner.ValidateAsync(provider, idToken, nonce, cancellationToken);
        }

        var name = idToken[TokenPrefix.Length..];
        return Task.FromResult(UserProfiles.HandlePattern().IsMatch(name)
            ? new ProviderIdentity(TokenPrefix + name, $"{name}@example.test", EmailVerified: true)
            : null);
    }

    public Task<IReadOnlyDictionary<string, object>?> ValidateProviderJwtAsync(
        string provider, string jwt, CancellationToken cancellationToken) =>
        inner.ValidateProviderJwtAsync(provider, jwt, cancellationToken);
}

/// <summary>Fails startup when developer sign-in is switched on outside Development.</summary>
public sealed class DevSignInGuard(IHostEnvironment environment) : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options) =>
        options.DevSignIn && !environment.IsDevelopment()
            ? ValidateOptionsResult.Fail(
                $"Auth:DevSignIn lets anyone sign in as anyone; it is only allowed in Development, not {environment.EnvironmentName}.")
            : ValidateOptionsResult.Success;
}
