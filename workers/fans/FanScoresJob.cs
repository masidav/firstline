using Dapper;
using FandomScore.Worker.Jobs;
using Npgsql;

namespace FandomScore.Worker.Fans;

/// <summary>
/// Hourly <c>fans.overall_scores</c>: recomputes every live fan's trust (behind the scenes; the draw breaks ties with
/// it) and fan level (1 to 5, shown to followers) with <c>refresh_fan_overall_scores()</c> (migration 0039). Idempotent.
/// The draw and a fan's own level page compute fresh instead of reading this table.
/// </summary>
public sealed partial class FanScoresJob(NpgsqlDataSource db, ILogger<FanScoresJob> logger) : IJobHandler
{
    public string Kind => "fans.overall_scores";

    public TimeSpan? Every => TimeSpan.FromHours(1);

    public async Task RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>("SELECT refresh_fan_overall_scores()", commandTimeout: 600);
        LogRefreshed(logger, count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed overall scores for {Count} fans.")]
    private static partial void LogRefreshed(ILogger logger, int count);
}
