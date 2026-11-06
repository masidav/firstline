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
/// Sends one email. The provider is an open decision (PROJECT_TODO "Choose an email provider for staff notifications"):
/// until then the only implementation is <see cref="LogEmailSender"/>, which writes the email to the worker's log.
/// A real provider (an SMTP relay or an HTTP API) is one class implementing this, picked by <c>Email__Provider</c>
/// in <see cref="EmailSetup"/>; nothing else changes.
/// </summary>
public interface IEmailSender
{
    Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>The provider couldn't take the request now (network, 5xx, rate limit); the job queue retries later.</summary>
public sealed class EmailUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
