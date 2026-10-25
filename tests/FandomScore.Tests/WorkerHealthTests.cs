using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FandomScore.Database;
using FandomScore.Worker.Hosting;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>The worker's liveness signal: job loops report each iteration, GET /healthz says whether they still do.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerHealthTests(PostgresFixture postgres)
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    [Fact]
    public void Alive_while_every_loop_keeps_reporting()
    {
        var clock = new TestClock();
        var heartbeat = new WorkerHeartbeat(clock);

        Assert.True(heartbeat.Report(StaleAfter).Alive, "nothing reported yet, just started");
        heartbeat.Beat(0);
        heartbeat.Beat(1);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(heartbeat.Report(StaleAfter).Alive);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(heartbeat.Report(StaleAfter).Alive);

        heartbeat.Beat(0);
        Assert.False(heartbeat.Report(StaleAfter).Alive, "loop 1 is still stuck");
        heartbeat.Beat(1);
        Assert.True(heartbeat.Report(StaleAfter).Alive);
    }

    [Fact]
    public void Stale_when_nothing_reported_since_startup()
    {
        var clock = new TestClock();
        var heartbeat = new WorkerHeartbeat(clock);

        clock.Advance(StaleAfter + TimeSpan.FromSeconds(1));

        Assert.False(heartbeat.Report(StaleAfter).Alive);
    }

    [Fact]
    public async Task Healthz_answers_200_then_503_once_a_loop_goes_quiet()
    {
        var clock = new TestClock();
        var heartbeat = new WorkerHeartbeat(clock);
        heartbeat.Beat(0);
        var port = FreePort();
        var options = Options.Create(new HealthOptions { Port = port, StaleAfter = StaleAfter });
        using var server = new WorkerHealthServer(heartbeat, options, NullLogger<WorkerHealthServer>.Instance);
        using var http = new HttpClient();

        await server.StartAsync(CancellationToken.None);
        try
        {
            using var ok = await GetWhenListeningAsync(http, port);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
            Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
            Assert.True(body.RootElement.GetProperty("loops").TryGetProperty("0", out _));

            clock.Advance(StaleAfter + TimeSpan.FromSeconds(1));
            using var stale = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/healthz"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, stale.StatusCode);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Running_job_loops_report_to_the_heartbeat()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"health_{Guid.NewGuid():N}");
        Assert.True(Migrator.Upgrade(connectionString).Successful);
        await using var db = NpgsqlDataSource.Create(connectionString);
        var options = Options.Create(new JobOptions { Concurrency = 2, PollInterval = TimeSpan.FromMilliseconds(50) });
        var heartbeat = new WorkerHeartbeat(TimeProvider.System);
        using var runner = new JobRunner(new JobQueue(db, options), [], options, TimeProvider.System,
            NullLogger<JobRunner>.Instance, heartbeat);

        await runner.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (heartbeat.Report(StaleAfter).Loops.Count < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }

        var report = heartbeat.Report(StaleAfter);
        Assert.Equal([0, 1], report.Loops.Keys.Order());
        Assert.True(report.Alive);
    }

    /// <summary>The listener accepts connections a moment after StartAsync returns; retry "connection refused" briefly.</summary>
    private static async Task<HttpResponseMessage> GetWhenListeningAsync(HttpClient http, int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return await http.GetAsync(new Uri($"http://127.0.0.1:{port}/healthz"));
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
