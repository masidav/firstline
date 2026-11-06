using System.Net;
using Microsoft.Extensions.Logging;

namespace FandomScore.MusicProviders;

/// <summary>
/// The retry policy both clients share: a short 429 is waited out, server errors and timeouts are retried a few
/// times, and longer waits surface as <see cref="MusicUnavailableException"/> so the job queue tries later. Same
/// rules as the Spotify client.
/// </summary>
internal static partial class ProviderHttp
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private const int MaxTries = 3;
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, TimeProvider time, ILogger logger, string provider, Func<HttpRequestMessage> createRequest, string label,
        bool retryServerErrors, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = createRequest();
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (!retryServerErrors || attempt >= MaxTries)
                {
                    throw new MusicUnavailableException($"{provider} {label} request failed or timed out.", inner: e);
                }

                LogRetry(logger, provider, label, 0, attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt), time, cancellationToken);
                continue;
            }

            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter switch
                {
                    { Delta: { } delta } => delta,
                    { Date: { } date } => date - time.GetUtcNow() is var w && w > TimeSpan.Zero ? w : TimeSpan.Zero,
                    _ => DefaultRetryAfter,
                };
                response.Dispose();
                if (wait > MaxInlineWait || attempt >= MaxTries)
                {
                    throw new MusicUnavailableException($"{provider} rate limited the {label} request.", wait);
                }

                LogRetry(logger, provider, label, status, attempt);
                await Task.Delay(wait, time, cancellationToken);
                continue;
            }

            if (status >= 500)
            {
                response.Dispose();
                if (!retryServerErrors || attempt >= MaxTries)
                {
                    throw new MusicUnavailableException($"{provider} returned {status} for the {label} request.");
                }

                LogRetry(logger, provider, label, status, attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt), time, cancellationToken);
                continue;
            }

            return response;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} {Request} request got status {Status} on attempt {Attempt}; retrying.")]
    private static partial void LogRetry(ILogger logger, string provider, string request, int status, int attempt);
}
