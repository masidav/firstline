using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace FandomScore.Worker.Notifications.Email;

/// <summary>Email settings (<c>Email</c> section). Off by default: nothing is sent until <c>Email__Enabled=true</c>.</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Each email goes to the worker's log. For development only.</summary>
    public const string LogProvider = "log";

    /// <summary>
    /// An SMTP relay with a login (<see cref="SmtpEmailSender"/>): Gmail's, since the owner decided on 2026-11-07 that
    /// everything is sent from the account d57590971@gmail.com (an app password; no domain to verify).
    /// </summary>
    public const string SmtpProvider = "smtp";

    /// <summary>False (the default) sends nothing; staff notices stay in-app and as pushes.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which sender: <c>log</c> or <c>smtp</c>. Required when enabled.</summary>
    public string Provider { get; set; } = LogProvider;

    /// <summary>The From address, e.g. <c>Fandom Score &lt;no-reply@example.com&gt;</c>. Required when enabled.</summary>
    public string From { get; set; } = "";

    /// <summary>
    /// The fan web app's origin, e.g. <c>https://app.beta.firstline-5730kl.com</c>: each email links to the screen the
    /// notice is about (<c>{AppOrigin}{link}</c>, the same <c>link</c> the app opens for a push). Required when enabled.
    /// </summary>
    public string AppOrigin { get; set; } = "";

    /// <summary>A notice still unsent this long after it was written is not emailed (it would arrive stale).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The relay, for <c>Email__Provider=smtp</c> (<c>Email__Smtp__*</c>).</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>The bare address in <see cref="From"/> (without a display name), or null when it isn't one.</summary>
    public string? FromAddress
    {
        get
        {
            try
            {
                return new MailAddress(From).Address;
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>Why the settings can't be used, or null when they can. Checked at startup.</summary>
    public string? Problem()
    {
        if (!Enabled)
        {
            return null;
        }

        if (Provider is not (LogProvider or SmtpProvider))
        {
            return $"Email__Provider '{Provider}' is not one of: {LogProvider}, {SmtpProvider}.";
        }

        if (FromAddress is null)
        {
            return "Email__From must be an email address, e.g. 'Fandom Score <no-reply@example.com>'.";
        }

        if (!Uri.TryCreate(AppOrigin, UriKind.Absolute, out var origin) || origin.Scheme is not ("https" or "http")
            || origin.PathAndQuery != "/" || !string.IsNullOrEmpty(origin.Fragment))
        {
            return "Email__AppOrigin must be the web app's origin, e.g. https://app.beta.firstline-5730kl.com (no path).";
        }

        return Provider == SmtpProvider ? Smtp.Problem() : null;
    }

    /// <summary>The origin without a trailing slash, for <c>{AppOrigin}{link}</c>.</summary>
    public string AppOriginTrimmed => AppOrigin.TrimEnd('/');
}

/// <summary>
/// SMTP relay settings (<c>Email__Smtp__*</c>). Defaults fit Gmail: STARTTLS on port 587, the login is the From
/// address, and the password is an app password (Google account, 2-Step Verification, App passwords), stored as the
/// secret <c>Email__Smtp__Password</c>. Gmail sends as the signed-in account (or one of its verified aliases) whatever
/// the From header says, and a free account sends at most about 500 messages a day.
/// </summary>
public sealed class SmtpOptions
{
    public const string StartTls = "starttls";
    public const string Ssl = "ssl";
    public const string None = "none";

    public string Host { get; set; } = "smtp.gmail.com";

    public int Port { get; set; } = 587;

    /// <summary><c>starttls</c> (587), <c>ssl</c> (465, TLS from the first byte), or <c>none</c> (local test relays only).</summary>
    public string Security { get; set; } = StartTls;

    /// <summary>The login. Empty: the address part of <c>Email__From</c>.</summary>
    public string Username { get; set; } = "";

    /// <summary>Secret <c>Email__Smtp__Password</c>. Whitespace counts as unset (a Secret Manager version cannot be empty).</summary>
    public string Password { get; set; } = "";

    public bool HasPassword => !string.IsNullOrWhiteSpace(Password);

    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            return "Email__Smtp__Host is required for Email__Provider=smtp.";
        }

        if (Port is < 1 or > 65535)
        {
            return "Email__Smtp__Port must be between 1 and 65535 (587 for STARTTLS, 465 for SSL).";
        }

        if (Security is not (StartTls or Ssl or None))
        {
            return $"Email__Smtp__Security must be {StartTls}, {Ssl}, or {None}.";
        }

        if (!HasPassword)
        {
            return "Email__Smtp__Password is not set (a Secret Manager placeholder counts as unset): store the relay's app password before enabling email.";
        }

        return null;
    }
}

public static class EmailSetup
{
    /// <summary>Binds <see cref="EmailOptions"/>, refuses to start on bad settings, and registers the configured sender.</summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.Section))
            .Validate(options => options.Problem() is null, "Email settings are invalid (see the Email section in workers/notifications/README.md).")
            .ValidateOnStart();
        services.AddSingleton<IEmailSender>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmailOptions>>().Value;
            return options.Provider switch
            {
                EmailOptions.LogProvider => new LogEmailSender(options, sp.GetRequiredService<ILogger<LogEmailSender>>()),
                EmailOptions.SmtpProvider => new SmtpEmailSender(options, sp.GetRequiredService<ILogger<SmtpEmailSender>>()),
                _ => throw new InvalidOperationException(options.Problem() ?? $"No email sender named '{options.Provider}'."),
            };
        });
        return services;
    }
}
