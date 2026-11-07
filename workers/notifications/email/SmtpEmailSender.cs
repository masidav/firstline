using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace FandomScore.Worker.Notifications.Email;

/// <summary>
/// Sends through an SMTP relay with a login (<c>Email__Provider=smtp</c>, <see cref="SmtpOptions"/>): one connection per
/// message, which is fine at the staff-notice volume. Gmail's relay is the one in use (decided 2026-11-07; the From
/// address is the owner's account). A permanent refusal of the sender, the recipient, or the message (a 5xx reply)
/// is a final <see cref="EmailResult"/>; everything else (unreachable, a 4xx, a refused login, a dropped connection)
/// throws <see cref="EmailUnavailableException"/> so the job queue retries. A refused login is retried on purpose:
/// it is a secret to fix, after which the queued notices should still go out.
/// </summary>
public sealed partial class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _smtp = options.Smtp;

    public async Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (!MailboxAddress.TryParse(message.To, out var to))
        {
            return new EmailResult(false, Error: "The address is not valid.");
        }

        var from = MailboxAddress.Parse(options.From);
        var mime = new MimeMessage();
        mime.From.Add(from);
        mime.To.Add(to);
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Text };
        mime.MessageId = MimeUtils.GenerateMessageId(from.Domain);

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(_smtp.Host, _smtp.Port, Security(_smtp.Security), cancellationToken);
            await client.AuthenticateAsync(Username(), _smtp.Password, cancellationToken);
            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (SmtpCommandException e) when ((int)e.StatusCode >= 500 && e.ErrorCode is SmtpErrorCode.SenderNotAccepted
            or SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted)
        {
            // Final for this message: a bad recipient, a sender the relay won't send as, or content it refuses.
            LogRefused(logger, mime.MessageId, (int)e.StatusCode, e.ErrorCode.ToString(), e.Message);
            return new EmailResult(false, mime.MessageId, $"{(int)e.StatusCode} {e.ErrorCode}: {e.Message}");
        }
        catch (AuthenticationException e)
        {
            throw new EmailUnavailableException($"The SMTP relay {_smtp.Host} refused the login (check Email__Smtp__Password).", e);
        }
        catch (Exception e) when (e is SmtpCommandException or SmtpProtocolException or SocketException or IOException)
        {
            throw new EmailUnavailableException($"The SMTP relay {_smtp.Host} could not take the message: {e.Message}", e);
        }

        return new EmailResult(true, mime.MessageId);
    }

    /// <summary>The login: <c>Email__Smtp__Username</c>, or the From address.</summary>
    private string Username() =>
        string.IsNullOrWhiteSpace(_smtp.Username) ? options.FromAddress ?? "" : _smtp.Username;

    private static SecureSocketOptions Security(string setting) => setting switch
    {
        SmtpOptions.Ssl => SecureSocketOptions.SslOnConnect,
        SmtpOptions.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email {MessageId} refused by the relay: {Status} {Code} {Reply}")]
    private static partial void LogRefused(ILogger logger, string messageId, int status, string code, string reply);
}
