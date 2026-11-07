using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FandomScore.Worker.Hosting;

/// <summary>
/// Answers on <c>Health__Port</c>, plain HTTP, not exposed by a Service:
/// <list type="bullet">
/// <item><c>GET /healthz</c>: 200 while the job loops are reporting and 503 once one has gone quiet for
/// <c>Health__StaleAfter</c> (<see cref="WorkerHeartbeat"/>). The pod's liveness probe.</item>
/// <item><c>GET /healthz/integrations</c>: probes every <see cref="IIntegrationCheck"/> with the configured
/// credentials (one real request each) and answers 200 when each is ok or off, 503 when one is refused or
/// unavailable. The deploy script calls it through a port-forward after the rollout (infrastructure/k8s/deploy.sh).</item>
/// </list>
/// Requests are served concurrently, so a slow probe never delays the liveness answer.
/// </summary>
public sealed partial class WorkerHealthServer(
    WorkerHeartbeat heartbeat, IntegrationChecks integrations, IOptions<HealthOptions> options, ILogger<WorkerHealthServer> logger)
    : BackgroundService
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

            _ = RespondAsync(context, stoppingToken);
        }
    }

    private async Task RespondAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        int status;
        byte[] body;
        try
        {
            (status, body) = context.Request.Url?.AbsolutePath.TrimEnd('/') == "/healthz/integrations"
                ? await IntegrationsAsync(cancellationToken)
                : Liveness();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A probe's own failure modes are mapped to states; anything else is a bug worth seeing in the logs.
            LogRequestFailed(logger, e, context.Request.Url?.AbsolutePath);
            status = (int)HttpStatusCode.InternalServerError;
            body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { status = "error" }, Json));
        }

        try
        {
            using var response = context.Response;
            response.StatusCode = status;
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, cancellationToken);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or OperationCanceledException)
        {
            // The probe gave up waiting; nothing to do.
        }
    }

    private (int Status, byte[] Body) Liveness()
    {
        var report = heartbeat.Report(options.Value.StaleAfter);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            status = report.Alive ? "ok" : "stale",
            started_at = report.StartedAt,
            stale_after = options.Value.StaleAfter,
            loops = report.Loops.ToDictionary(l => l.Key.ToString(CultureInfo.InvariantCulture), l => l.Value),
        }, Json));
        return (report.Alive ? (int)HttpStatusCode.OK : (int)HttpStatusCode.ServiceUnavailable, body);
    }

    private async Task<(int Status, byte[] Body)> IntegrationsAsync(CancellationToken cancellationToken)
    {
        var results = await integrations.RunAsync(cancellationToken);
        var overall = IntegrationChecks.Overall(results.Values);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            status = StateName(overall),
            integrations = results.ToDictionary(r => r.Key, r => new { status = StateName(r.Value.State), detail = r.Value.Detail }, StringComparer.Ordinal),
        }, Json));
        var healthy = overall is IntegrationState.Ok or IntegrationState.Off;
        return (healthy ? (int)HttpStatusCode.OK : (int)HttpStatusCode.ServiceUnavailable, body);
    }

    private static string StateName(IntegrationState state) => state switch
    {
        IntegrationState.Off => "off",
        IntegrationState.Ok => "ok",
        IntegrationState.Refused => "refused",
        IntegrationState.Unavailable => "unavailable",
        _ => "unknown",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Health endpoint listening on port {Port} (GET /healthz, GET /healthz/integrations).")]
    private static partial void LogListening(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Error, Message = "Health request {Path} failed.")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception, string? path);
}
