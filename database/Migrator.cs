using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;

namespace FandomScore.Database;

/// <summary>Applies the SQL migrations embedded from <c>database/migrations</c>.</summary>
public static class Migrator
{
    public static DatabaseUpgradeResult Upgrade(string connectionString, IUpgradeLog? log = null) =>
        DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(Migrator).Assembly, name => name.EndsWith(".sql", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogTo(log ?? new ConsoleUpgradeLog())
            .Build()
            .PerformUpgrade();
}
