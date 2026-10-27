using System.Globalization;
using System.Text.Json;

namespace FandomScore.Notifications;

/// <summary>What a fan reads: a title, a sentence, and the app screen it opens.</summary>
public sealed record NotificationMessage(string Title, string Body, string Link);

/// <summary>
/// Turns a notification row (kind and data, migrations 0021, 0029, 0031) into text. The API shows it in the in-app list;
/// the worker sends the same text as a push. Times are relative ("starts in about 5 hours") or dates only, because
/// neither knows the fan's time zone.
/// </summary>
public static class NotificationText
{
    /// <summary>Push categories a fan can turn off (users.push_tickets, push_presales, push_staff; migration 0032).</summary>
    public static string Category(string kind) => kind switch
    {
        "draw_selected" or "draw_not_selected" or "seat_backfilled" or "ticket_delivered" => "tickets",
        "presale" => "presales",
        _ when kind.StartsWith("staff_", StringComparison.Ordinal) => "staff",
        _ => "other",
    };

    public static NotificationMessage Describe(string kind, JsonElement data, DateTime now)
    {
        string Text(string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
                                    && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        int Number(string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
                                   && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
        var show = Text("event_name") is { Length: > 0 } name ? name : "the show";
        var when = Time(data, "starts_at") is { } start ? start.ToString("MMM d", CultureInfo.InvariantCulture) : null;
        var on = when is null ? "" : $" on {when}";
        var seats = Number("seats") == 2 ? "seats for you and your +1" : "a seat";
        var (title, body, link) = kind switch
        {
            "draw_selected" => ("You got tickets!", $"The fan draw for {show}{on} picked you: {seats} at face value. Your ticket arrives shortly before the show.", "/tickets"),
            "draw_not_selected" => ("Not this time", $"The fan draw for {show} didn't pick you. If a selected fan returns their seat, it goes to the next in line.", $"/show/{Text("event_id")}"),
            "seat_backfilled" => ("A seat opened up for you", $"A fan returned their seat for {show}{on}, and it's yours: {seats} at face value.", "/tickets"),
            "ticket_delivered" => ("Your ticket is here", $"Your ticket for {show}{on} has been delivered. Open Tickets to show it at the door.", "/tickets"),
            "presale" => (Text("artist_name") is { Length: > 0 } artist ? $"Presale: {artist}" : "Presale",
                $"{Text("presale_name")} for {show}{(Text("city") is { Length: > 0 } city ? $" in {city}" : "")} {PresaleWhen(data, now)}.",
                $"/show/{Text("event_id")}"),
            "staff_report" => ("A post was reported", $"A {(Text("target_type") == "photo" ? "photo" : "album log")} was reported ({Text("reason")}).", "/moderation"),
            "staff_review" => ("Proof to review", $"@{Text("handle")} sent {Text("kind").Replace('_', ' ')} for {Text("artist_name")}.", "/review"),
            "staff_redaction" => ("A photo needs a redaction check", Text("note") is { Length: > 0 } note ? note : "The redaction worker couldn't finish a file.", "/redactions"),
            "staff_show_changed" => ("A show with fan tickets changed", ShowChanged(show, Text("status"), data), $"/show/{Text("event_id")}"),
            "staff_draw" => ("A fan draw ran", $"{show}: {Number("selected_fans")} of {Number("registrations")} fans selected, {Number("seats_assigned")} of {Number("tickets")} seats assigned.", $"/show/{Text("event_id")}"),
            _ => ("Update", "Something changed.", "/home"),
        };
        return new NotificationMessage(title, body, link);
    }

    /// <summary>What Ticketmaster changed about a show with a fan allocation (migration 0036), times in UTC.</summary>
    private static string ShowChanged(string show, string status, JsonElement data)
    {
        static string At(DateTime? time) => time is { } t ? t.ToString("MMM d, HH:mm 'UTC'", CultureInfo.InvariantCulture) : "an unknown time";
        var moved = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("moved", out var m) && m.ValueKind == JsonValueKind.True;
        var what = status switch
        {
            "cancelled" => $"Ticketmaster lists {show} as cancelled.",
            "postponed" => $"Ticketmaster lists {show} as postponed.",
            _ when moved => $"Ticketmaster moved {show} to {At(Time(data, "source_starts_at"))}.",
            _ => $"Ticketmaster lists {show} as rescheduled.",
        };
        return $"{what} Its fan allocation still uses {At(Time(data, "starts_at"))}: update the fans or the allocation.";
    }

    /// <summary>When a presale opens, relative to <paramref name="now"/> ("starts in about 5 hours", "is on now").</summary>
    public static string PresaleWhen(JsonElement data, DateTime now)
    {
        if (Time(data, "ends_at") is { } ends && ends <= now)
        {
            return "has ended";
        }

        if (Time(data, "starts_at") is not { } starts || starts <= now)
        {
            return "is on now";
        }

        var hours = (starts - now).TotalHours;
        return hours switch
        {
            < 1 => "starts within the hour",
            < 1.5 => "starts in about an hour",
            < 36 => $"starts in about {Math.Round(hours)} hours",
            _ => $"starts in {Math.Round(hours / 24)} days",
        };
    }

    private static DateTime? Time(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
}
