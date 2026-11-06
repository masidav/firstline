using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace FandomScore.Worker.Notifications.Email;

/// <summary>Email settings (<c>Email</c> section). Off by default: nothing is sent until <c>Email__Enabled=true</c>.</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>The one sender that exists today: each email goes to the worker's log. For development only.</summary>
    public const string LogProvider = "log";

    /// <summary>False (the default) sends nothing; staff notices stay in-app and as pushes.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which sender: <c>log</c>. A provider's client, once chosen, adds its name here. Required when enabled.</summary>
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

    /// <summary>Why the settings can't be used, or null when they can. Checked at startup.</summary>
    public string? Problem()
    {
        if (!Enabled)
        {
            return null;
        }

        if (Provider != LogProvider)
        {
            return $"Email__Provider '{Provider}' is not one of: {LogProvider}.";
        }

        try
        {
            _ = new MailAddress(From);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return "Email__From must be an email address, e.g. 'Fandom Score <no-reply@example.com>'.";
        }

        if (!Uri.TryCreate(AppOrigin, UriKind.Absolute, out var origin) || origin.Scheme is not ("https" or "http")
            || origin.PathAndQuery != "/" || !string.IsNullOrEmpty(origin.Fragment))
        {
            return "Email__AppOrigin must be the web app's origin, e.g. https://app.beta.firstline-5730kl.com (no path).";
        }

        return null;
    }

    /// <summary>The origin without a trailing slash, for <c>{AppOrigin}{link}</c>.</summary>
    public string AppOriginTrimmed => AppOrigin.TrimEnd('/');
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
                _ => throw new InvalidOperationException(options.Problem() ?? $"No email sender named '{options.Provider}'."),
            };
        });
        return services;
    }
}
