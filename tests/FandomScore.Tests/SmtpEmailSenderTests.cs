using System.Net;
using System.Net.Sockets;
using System.Text;
using FandomScore.Worker.Notifications.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace FandomScore.Tests;

/// <summary>
/// The SMTP sender against a tiny in-process relay that speaks just enough SMTP (EHLO, AUTH PLAIN, MAIL, RCPT, DATA,
/// QUIT): the login and the message reach the relay, a 5xx on the recipient is a final result, and an unreachable or
/// refusing relay is retried through <see cref="EmailUnavailableException"/>.
/// </summary>
public sealed class SmtpEmailSenderTests
{
    private static EmailOptions Options(int port, string password = "app-password", string username = "") => new()
    {
        Enabled = true,
        Provider = EmailOptions.SmtpProvider,
        From = "Fandom Score <d57590971@gmail.com>",
        AppOrigin = "https://app.test",
        Smtp = { Host = "127.0.0.1", Port = port, Security = SmtpOptions.None, Password = password, Username = username },
    };

    [Fact]
    public async Task Sends_with_the_From_address_as_login_and_returns_the_message_id()
    {
        await using var relay = await FakeRelay.StartAsync();
        var sender = new SmtpEmailSender(Options(relay.Port), NullLogger<SmtpEmailSender>.Instance);

        var result = await sender.SendAsync(new EmailMessage("staff@example.test", "A show moved", "Geese at Metro moved to Friday."), CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.MessageId);
        Assert.EndsWith("@gmail.com", result.MessageId, StringComparison.Ordinal);
        Assert.Equal(("d57590971@gmail.com", "app-password"), relay.Login);
        Assert.Equal("d57590971@gmail.com", relay.MailFrom);
        Assert.Equal("staff@example.test", relay.RcptTo);
        Assert.Contains("Subject: A show moved", relay.Data, StringComparison.Ordinal);
        Assert.Contains("Geese at Metro moved to Friday.", relay.Data, StringComparison.Ordinal);
        Assert.Contains($"Message-Id: <{result.MessageId}>", relay.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_username_wins_over_the_From_address()
    {
        await using var relay = await FakeRelay.StartAsync();
        var sender = new SmtpEmailSender(Options(relay.Port, username: "relay-user"), NullLogger<SmtpEmailSender>.Instance);

        var result = await sender.SendAsync(new EmailMessage("staff@example.test", "s", "t"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(("relay-user", "app-password"), relay.Login);
    }

    [Fact]
    public async Task A_recipient_the_relay_refuses_for_good_is_a_final_result_not_a_retry()
    {
        await using var relay = await FakeRelay.StartAsync(refuseRecipient: "550 5.1.1 No such user");
        var sender = new SmtpEmailSender(Options(relay.Port), NullLogger<SmtpEmailSender>.Instance);

        var result = await sender.SendAsync(new EmailMessage("nobody@example.test", "s", "t"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("550", result.Error, StringComparison.Ordinal);
        Assert.Contains("No such user", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_temporary_refusal_is_retried()
    {
        await using var relay = await FakeRelay.StartAsync(refuseRecipient: "450 4.2.1 Try again later");
        var sender = new SmtpEmailSender(Options(relay.Port), NullLogger<SmtpEmailSender>.Instance);

        await Assert.ThrowsAsync<EmailUnavailableException>(() => sender.SendAsync(new EmailMessage("staff@example.test", "s", "t"), CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_login_is_retried_so_a_fixed_secret_lets_the_queue_drain()
    {
        await using var relay = await FakeRelay.StartAsync(acceptedPassword: "the-real-one");
        var sender = new SmtpEmailSender(Options(relay.Port, password: "wrong"), NullLogger<SmtpEmailSender>.Instance);

        var e = await Assert.ThrowsAsync<EmailUnavailableException>(() => sender.SendAsync(new EmailMessage("staff@example.test", "s", "t"), CancellationToken.None));
        Assert.Contains("Email__Smtp__Password", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_relay_is_retried()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var sender = new SmtpEmailSender(Options(port), NullLogger<SmtpEmailSender>.Instance);

        await Assert.ThrowsAsync<EmailUnavailableException>(() => sender.SendAsync(new EmailMessage("staff@example.test", "s", "t"), CancellationToken.None));
    }

    [Fact]
    public async Task A_bad_recipient_address_is_final_without_talking_to_the_relay()
    {
        var sender = new SmtpEmailSender(Options(1), NullLogger<SmtpEmailSender>.Instance);

        var result = await sender.SendAsync(new EmailMessage("not an address", "s", "t"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    /// <summary>One connection at a time, plain text, AUTH PLAIN only; records what the client sent.</summary>
    private sealed class FakeRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string? _refuseRecipient;
        private readonly string _acceptedPassword;
        private readonly CancellationTokenSource _stop = new();
        private Task? _serving;

        private FakeRelay(TcpListener listener, string? refuseRecipient, string acceptedPassword)
        {
            _listener = listener;
            _refuseRecipient = refuseRecipient;
            _acceptedPassword = acceptedPassword;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public (string User, string Password)? Login { get; private set; }

        public string? MailFrom { get; private set; }

        public string? RcptTo { get; private set; }

        public string Data { get; private set; } = "";

        public static Task<FakeRelay> StartAsync(string? refuseRecipient = null, string acceptedPassword = "app-password")
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var relay = new FakeRelay(listener, refuseRecipient, acceptedPassword);
            relay._serving = relay.ServeAsync();
            return Task.FromResult(relay);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            if (_serving is not null)
            {
                try
                {
                    await _serving;
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
                {
                    // Shutting down.
                }
            }

            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new ASCIIEncoding(), 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

            await writer.WriteLineAsync("220 fake.relay ESMTP");
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                    case "HELO":
                        await writer.WriteLineAsync("250-fake.relay");
                        await writer.WriteLineAsync("250-AUTH PLAIN");
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "AUTH":
                        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(line.Split(' ')[2])).Split('\0');
                        Login = (parts[1], parts[2]);
                        await writer.WriteLineAsync(parts[2] == _acceptedPassword ? "235 2.7.0 Accepted" : "535 5.7.8 Username and Password not accepted");
                        break;
                    case "MAIL":
                        MailFrom = Address(line);
                        await writer.WriteLineAsync("250 2.1.0 OK");
                        break;
                    case "RCPT":
                        RcptTo = Address(line);
                        await writer.WriteLineAsync(_refuseRecipient ?? "250 2.1.5 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 Go ahead");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(_stop.Token) is { } dataLine && dataLine != ".")
                        {
                            data.Append(dataLine).Append("\r\n");
                        }

                        Data = data.ToString();
                        await writer.WriteLineAsync("250 2.0.0 OK queued");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }

        private static string Address(string line)
        {
            var start = line.IndexOf('<', StringComparison.Ordinal) + 1;
            return line[start..line.IndexOf('>', StringComparison.Ordinal)];
        }
    }
}
