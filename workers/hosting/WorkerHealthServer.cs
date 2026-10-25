using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FandomScore.Worker.Hosting;

/// <summary>
/// Answers <c>GET /healthz</c> on <c>Health__Port</c> with 200 while the job loops are reporting and 503 once one
/// has gone quiet for <c>Health__StaleAfter</c> (<see cref="WorkerHeartbeat"/>). Plain HTTP, meant for the pod's
/// liveness probe only; the port is not exposed by a Service.
/// </summary>
public sealed partial class WorkerHealthServer(
    WorkerHeartbeat heartbeat, IOptions<HealthOptions> options, ILogger<WorkerHealthServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.Value.Port;
        if (port <= 0)
        {
            return;
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port.ToString(CultureInfo.InvariantCulture)}/");
        listener.Start();
        LogListening(logger, port);
        using var stop = stoppingToken.Register(listener.Stop);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException && stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await RespondAsync(context, stoppingToken);
        }
    }

    private async Task RespondAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var report = heartbeat.Report(options.Value.StaleAfter);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            status = report.Alive ? "ok" : "stale",
            started_at = report.StartedAt,
            stale_after = options.Value.StaleAfter,
            loops = report.Loops.ToDictionary(l => l.Key.ToString(CultureInfo.InvariantCulture), l => l.Value),
        }, Json));

        try
        {
            using var response = context.Response;
            response.StatusCode = report.Alive ? (int)HttpStatusCode.OK : (int)HttpStatusCode.ServiceUnavailable;
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, cancellationToken);
        }
        catch (Exception e) when (e is HttpListenerException or IOException)
        {
            // The probe gave up waiting; nothing to do.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Liveness endpoint listening on port {Port} (GET /healthz).")]
    private static partial void LogListening(ILogger logger, int port);
}
