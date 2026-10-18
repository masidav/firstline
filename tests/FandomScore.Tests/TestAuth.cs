using System.Collections.Concurrent;
using System.Security.Cryptography;
using FandomScore.Api.Features.Authentication;
using Microsoft.Extensions.Logging;

namespace FandomScore.Tests;

/// <summary>Shared auth settings and fakes for API tests.</summary>
public static class TestAuth
{
    public static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>An identity token the fake validator accepts: <c>ok:subject:email</c> or <c>unverified:subject:email</c>.</summary>
    public static string IdToken(string subject, string email, bool verified = true) =>
        $"{(verified ? "ok" : "unverified")}:{subject}:{email}";
}

/// <summary>Stands in for Apple and Google. Real token validation is covered by OidcIdentityTokenValidatorTests.</summary>
public sealed class FakeIdentityTokenValidator : IIdentityTokenValidator
{
    public bool IsEnabled(string provider) => provider is "apple" or "google";

    public Task<ProviderIdentity?> ValidateAsync(string provider, string idToken, string? nonce, CancellationToken cancellationToken)
    {
        var parts = idToken.Split(':');
        ProviderIdentity? identity = parts is [var kind and ("ok" or "unverified"), var subject, var email]
            ? new ProviderIdentity(subject, email, kind == "ok")
            : null;
        return Task.FromResult(identity);
    }
}

/// <summary>Keeps every log message so tests can check that no credential is ever logged.</summary>
public sealed class CapturingLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = messages;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            provider.Messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
