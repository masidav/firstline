using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FandomScore.Worker.Concerts;

/// <summary>Configuration section <c>Ticketmaster</c>. The API key is a secret (<c>Ticketmaster__ApiKey</c>).</summary>
public sealed class TicketmasterOptions
{
    public const string Section = "Ticketmaster";

    /// <summary>Discovery API key (developer.ticketmaster.com). Empty turns Ticketmaster syncing off.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Comma-separated ISO countries whose shows are kept (the hub cities are in the US and Canada).</summary>
    public string Countries { get; set; } = "US,CA";

    /// <summary>How often a linked artist's shows are reloaded.</summary>
    public TimeSpan SyncEvery { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often an artist with no Ticketmaster match is looked up again.</summary>
    public TimeSpan RecheckUnmatchedEvery { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Artist syncs queued per hourly schedule run. Each costs about two calls; the default Discovery quota is 5,000
    /// calls a day.
    /// </summary>
    public int MaxSyncsPerHour { get; set; } = 100;

    public bool IsEnabled => ApiKey.Length > 0;

    public IReadOnlySet<string> CountrySet =>
        Countries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
}

/// <summary>Ticketmaster had a problem or asked us to slow down; the job queue retries later.</summary>
public sealed class TicketmasterUnavailableException(string message) : Exception(message);

/// <summary>A Ticketmaster artist ("attraction") and the Spotify artist ids it links to.</summary>
public sealed record TicketmasterAttraction(string Id, string Name, IReadOnlyList<string> SpotifyArtistIds);

public sealed record TicketmasterPresale(string Name, DateTimeOffset StartsAt, DateTimeOffset? EndsAt);

/// <summary>
/// One show. <see cref="StartsAt"/> is set when Ticketmaster gives a time; otherwise <see cref="LocalDate"/> (and
/// maybe <see cref="LocalTime"/>) with <see cref="Timezone"/>, converted by the database.
/// </summary>
public sealed record TicketmasterEvent(
    string Id, string Name, string? Url, string Status, DateTimeOffset? StartsAt, DateOnly? LocalDate, TimeOnly? LocalTime,
    bool TimeTba, string? Timezone, string? Venue, string? City, string? Region, string? Country,
    DateTimeOffset? PublicSaleStartsAt, DateTimeOffset? PublicSaleEndsAt, IReadOnlyList<TicketmasterPresale> Presales,
    IReadOnlyList<string> AttractionIds);

/// <summary>
/// The Ticketmaster Discovery API v2 (https://developer.ticketmaster.com/products-and-docs/apis/discovery-api/v2/):
/// attraction search and lookup, and an attraction's upcoming events. Requests are spaced to stay under the 5 per
/// second limit (per process). A 429 or server error throws <see cref="TicketmasterUnavailableException"/> so the
/// job retries later. The API key is never logged or put in exception messages.
/// </summary>
public sealed class TicketmasterClient(HttpClient http, IOptions<TicketmasterOptions> options)
{
    public static readonly Uri ApiBase = new("https://app.ticketmaster.com/discovery/v2/");
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Ticketmaster returns at most the first 1,000 results of a search (size × page &lt; 1000).</summary>
    private const int PageSize = 200;
    private const int MaxPages = 5;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(250);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    private readonly TicketmasterOptions _options = options.Value;

    public bool IsEnabled => _options.IsEnabled;

    /// <summary>Music attractions matching <paramref name="name"/>, best first (at most 20).</summary>
    public async Task<IReadOnlyList<TicketmasterAttraction>> SearchAttractionsAsync(string name, CancellationToken cancellationToken)
    {
        using var json = await GetAsync(
            $"attractions.json?keyword={Uri.EscapeDataString(name)}&classificationName=music&size=20", cancellationToken);
        return json is null ? [] : Embedded(json.RootElement, "attractions").Select(ParseAttraction).ToList();
    }

    /// <summary>The attraction, or null when Ticketmaster has no attraction with this id.</summary>
    public async Task<TicketmasterAttraction?> GetAttractionAsync(string id, CancellationToken cancellationToken)
    {
        using var json = await GetAsync($"attractions/{Uri.EscapeDataString(id)}.json", cancellationToken);
        return json is null ? null : ParseAttraction(json.RootElement);
    }

    /// <summary>The attraction's upcoming events in date order (up to 1,000).</summary>
    public async Task<IReadOnlyList<TicketmasterEvent>> GetUpcomingEventsAsync(
        string attractionId, DateTimeOffset from, CancellationToken cancellationToken)
    {
        var events = new List<TicketmasterEvent>();
        var start = from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        for (var page = 0; page < MaxPages; page++)
        {
            using var json = await GetAsync(
                $"events.json?attractionId={Uri.EscapeDataString(attractionId)}&startDateTime={start}&sort=date,asc&size={PageSize}&page={page}",
                cancellationToken);
            if (json is null)
            {
                break;
            }

            events.AddRange(Embedded(json.RootElement, "events").Select(ParseEvent).OfType<TicketmasterEvent>());
            var totalPages = json.RootElement.TryGetProperty("page", out var p) && p.TryGetProperty("totalPages", out var t)
                ? t.GetInt32() : 0;
            if (page + 1 >= totalPages)
            {
                break;
            }
        }

        return events;
    }

    private async Task<JsonDocument?> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var uri = new Uri(ApiBase, $"{pathAndQuery}{separator}apikey={Uri.EscapeDataString(_options.ApiKey)}");
        await Gate.WaitAsync(cancellationToken);
        HttpResponseMessage response;
        try
        {
            var wait = _lastRequest + MinSpacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            _lastRequest = DateTime.UtcNow;
            response = await http.GetAsync(uri, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new TicketmasterUnavailableException("Ticketmaster could not be reached.");
        }
        finally
        {
            Gate.Release();
        }

        using (response)
        {
            var path = pathAndQuery.Split('?')[0];
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return null;
                case HttpStatusCode.TooManyRequests:
                    throw new TicketmasterUnavailableException("Ticketmaster rate limit reached (429).");
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new TicketmasterUnavailableException($"Ticketmaster refused the API key ({(int)response.StatusCode}) for {path}.");
                case >= HttpStatusCode.InternalServerError:
                    throw new TicketmasterUnavailableException($"Ticketmaster error {(int)response.StatusCode} for {path}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TicketmasterUnavailableException($"Ticketmaster answered {(int)response.StatusCode} for {path}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
    }

    private static List<JsonElement> Embedded(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("_embedded", out var embedded)
        && embedded.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray()]
            : [];

    internal static TicketmasterAttraction ParseAttraction(JsonElement a)
    {
        var spotify = new List<string>();
        if (a.TryGetProperty("externalLinks", out var links) && links.TryGetProperty("spotify", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in list.EnumerateArray())
            {
                if (Text(link, "url") is { } url && SpotifyArtistId(url) is { } id)
                {
                    spotify.Add(id);
                }
            }
        }

        return new TicketmasterAttraction(Text(a, "id") ?? "", Text(a, "name") ?? "", spotify);
    }

    /// <summary>The artist id in an open.spotify.com/artist/{id} link (with or without a query or locale prefix).</summary>
    public static string? SpotifyArtistId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(parts, "artist");
        return at >= 0 && at + 1 < parts.Length && parts[at + 1].Length == 22 ? parts[at + 1] : null;
    }

    /// <summary>One event, or null when it has no id or no date at all (date to be announced).</summary>
    internal static TicketmasterEvent? ParseEvent(JsonElement e)
    {
        var id = Text(e, "id");
        if (id is null || !e.TryGetProperty("dates", out var dates))
        {
            return null;
        }

        var start = dates.TryGetProperty("start", out var s) ? s : default;
        var startsAt = Time(start, "dateTime");
        var localDate = Text(start, "localDate") is { } d && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : (DateOnly?)null;
        var localTime = Text(start, "localTime") is { } lt && TimeOnly.TryParse(lt, CultureInfo.InvariantCulture, out var time)
            ? time : (TimeOnly?)null;
        if (startsAt is null && localDate is null)
        {
            return null;
        }

        var timeTba = Bool(start, "timeTBA") || Bool(start, "noSpecificTime") || (startsAt is null && localTime is null);
        var status = dates.TryGetProperty("status", out var st) ? Text(st, "code") : null;

        DateTimeOffset? publicStart = null, publicEnd = null;
        var presales = new List<TicketmasterPresale>();
        if (e.TryGetProperty("sales", out var sales))
        {
            if (sales.TryGetProperty("public", out var pub))
            {
                publicStart = Time(pub, "startDateTime");
                publicEnd = Time(pub, "endDateTime");
            }

            if (sales.TryGetProperty("presales", out var pre) && pre.ValueKind == JsonValueKind.Array)
            {
                foreach (var presale in pre.EnumerateArray())
                {
                    if (Text(presale, "name") is { Length: > 0 } name && Time(presale, "startDateTime") is { } from)
                    {
                        presales.Add(new TicketmasterPresale(name.Trim(), from, Time(presale, "endDateTime")));
                    }
                }
            }
        }

        var venue = Embedded(e, "venues").Cast<JsonElement?>().FirstOrDefault();
        var attractions = Embedded(e, "attractions").Select(a => Text(a, "id")).OfType<string>().ToList();
        var url = Text(e, "url") is { } u && u.StartsWith("https://", StringComparison.Ordinal) ? u : null;
        return new TicketmasterEvent(
            id, Text(e, "name") ?? "", url, NormalizeStatus(status), startsAt, localDate, localTime, timeTba,
            Text(dates, "timezone"),
            venue is { } v ? Text(v, "name") : null,
            venue is { } v1 && v1.TryGetProperty("city", out var city) ? Text(city, "name") : null,
            venue is { } v2 && v2.TryGetProperty("state", out var state) ? Text(state, "stateCode") : null,
            venue is { } v3 && v3.TryGetProperty("country", out var country) ? Text(country, "countryCode") : null,
            publicStart, publicEnd, presales.DistinctBy(p => (p.Name, p.StartsAt)).ToList(), attractions);
    }

    /// <summary>Ticketmaster's status codes; anything unknown counts as scheduled.</summary>
    private static string NormalizeStatus(string? code) => code?.ToLowerInvariant() switch
    {
        "onsale" => "onsale",
        "offsale" => "offsale",
        "canceled" or "cancelled" => "cancelled",
        "postponed" => "postponed",
        "rescheduled" => "rescheduled",
        _ => "scheduled",
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUniversalTime()
            : null;
}
