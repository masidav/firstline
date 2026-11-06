namespace FandomScore.Worker.Notifications.Email;

/// <summary>
/// Writes each email to the worker's log instead of sending it (<c>Email__Provider=log</c>). For development and for
/// checking the text before a provider exists: the whole email, address included, lands in the log, which is kept
/// 30 days on GCP, so don't use it there with real staff addresses.
/// </summary>
public sealed partial class LogEmailSender(EmailOptions options, ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        LogEmail(logger, id, options.From, message.To, message.Subject, message.Text);
        return Task.FromResult(new EmailResult(true, id));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email {MessageId} (not sent; Email__Provider=log)\nFrom: {From}\nTo: {To}\nSubject: {Subject}\n\n{Text}")]
    private static partial void LogEmail(ILogger logger, string messageId, string from, string to, string subject, string text);
}
