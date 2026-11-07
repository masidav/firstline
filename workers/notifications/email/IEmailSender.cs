namespace FandomScore.Worker.Notifications.Email;

/// <summary>One plain-text email to one address.</summary>
public sealed record EmailMessage(string To, string Subject, string Text);

/// <summary>
/// What the sender did. <see cref="Ok"/> false with an <see cref="Error"/> is a final answer for this message (a bad
/// address, a rejected sender); it is recorded and not retried. A provider that couldn't take the request at all
/// throws <see cref="EmailUnavailableException"/> instead, and the job queue retries.
/// </summary>
public sealed record EmailResult(bool Ok, string? MessageId = null, string? Error = null);

/// <summary>
/// Sends one email. Implementations: <see cref="LogEmailSender"/> (the worker's log, for development) and
/// <see cref="SmtpEmailSender"/> (an SMTP relay with a login; Gmail's on beta, decided 2026-11-07). Another provider
/// (an HTTP API) would be one more class implementing this, picked by <c>Email__Provider</c> in <see cref="EmailSetup"/>.
/// </summary>
public interface IEmailSender
{
    Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>The provider couldn't take the request now (network, 5xx, rate limit); the job queue retries later.</summary>
public sealed class EmailUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
