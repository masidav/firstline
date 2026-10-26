using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace FandomScore.Worker.Notifications;

/// <summary>Configuration section <c>Push</c>.</summary>
public sealed class PushOptions
{
    public const string Section = "Push";

    /// <summary>False stops all pushes (in-app notifications are unaffected).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Expo access token, needed only when "enhanced push security" is on for the Expo project. Secret
    /// (<c>Push__ExpoAccessToken</c>).
    /// </summary>
    public string ExpoAccessToken { get; set; } = "";

    /// <summary>A notification still unsent this long after it was written is not pushed (it would arrive stale).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(12);
}

/// <summary>Expo couldn't take the request now; the job queue retries later.</summary>
public sealed class ExpoPushUnavailableException(string message) : Exception(message);

/// <summary>One push to one device.</summary>
public sealed record PushMessage(string To, string Title, string Body, IReadOnlyDictionary<string, string> Data, int? Badge);

/// <summary>Expo's answer for one message, or for one receipt: ok (with a ticket id), or an error such as DeviceNotRegistered.</summary>
public sealed record PushResult(bool Ok, string? TicketId, string? Error);

/// <summary>
/// Expo's push service (https://docs.expo.dev/push-notifications/sending-notifications/): sends up to 100 messages per
/// request and reads receipts (up to 1,000 ids). A 429 or server error throws <see cref="ExpoPushUnavailableException"/>.
/// Tokens and the access token are never logged.
/// </summary>
public sealed class ExpoPushClient(HttpClient http, IOptions<PushOptions> options)
{
    public static readonly Uri SendUri = new("https://exp.host/--/api/v2/push/send");
    public static readonly Uri ReceiptsUri = new("https://exp.host/--/api/v2/push/getReceipts");
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    public const int MaxMessages = 100;
    public const int MaxReceipts = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly PushOptions _options = options.Value;

    /// <summary>Results in the order of <paramref name="messages"/>.</summary>
    public async Task<IReadOnlyList<PushResult>> SendAsync(IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken)
    {
        var results = new List<PushResult>();
        foreach (var batch in messages.Chunk(MaxMessages))
        {
            var body = batch.Select(m => new
            {
                to = m.To, title = m.Title, body = m.Body, data = m.Data, sound = "default", badge = m.Badge, priority = "high",
            });
            using var json = await PostAsync(SendUri, body, cancellationToken);
            var data = json.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d : default;
            for (var i = 0; i < batch.Length; i++)
            {
                results.Add(data.ValueKind == JsonValueKind.Array && i < data.GetArrayLength()
                    ? Parse(data[i])
                    : new PushResult(false, null, "NoResult"));
            }
        }

        return results;
    }

    /// <summary>Receipts by ticket id. Ids Expo doesn't know yet (or any more) are left out.</summary>
    public async Task<IReadOnlyDictionary<string, PushResult>> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken)
    {
        var receipts = new Dictionary<string, PushResult>(StringComparer.Ordinal);
        foreach (var batch in ticketIds.Chunk(MaxReceipts))
        {
            using var json = await PostAsync(ReceiptsUri, new { ids = batch }, cancellationToken);
            if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var receipt in data.EnumerateObject())
                {
                    receipts[receipt.Name] = Parse(receipt.Value);
                }
            }
        }

        return receipts;
    }

    private async Task<JsonDocument> PostAsync(Uri uri, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_options.ExpoAccessToken.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ExpoAccessToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new ExpoPushUnavailableException("Expo's push service could not be reached.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new ExpoPushUnavailableException($"Expo push answered {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                // 4xx: a bad request or a refused access token. Retrying won't help, but the queue records it.
                throw new ExpoPushUnavailableException($"Expo push refused the request ({(int)response.StatusCode}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
    }

    private static PushResult Parse(JsonElement item)
    {
        var status = item.TryGetProperty("status", out var s) ? s.GetString() : null;
        var id = item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        if (status == "ok")
        {
            return new PushResult(true, id, null);
        }

        var error = item.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : "Error";
        return new PushResult(false, id, error);
    }
}
