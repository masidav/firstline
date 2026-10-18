using FandomScore.Database;

namespace FandomScore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DatabaseTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_apply_once_and_rerun_as_no_op()
    {
        var connectionString = await postgres.CreateDatabaseAsync("migrations");

        var first = Migrator.Upgrade(connectionString);
        Assert.True(first.Successful, first.Error?.ToString());
        Assert.NotEmpty(first.Scripts);

        var second = Migrator.Upgrade(connectionString);
        Assert.True(second.Successful, second.Error?.ToString());
        Assert.Empty(second.Scripts);
    }

    // database/tests/invariants.sql uses psql meta-commands, so it runs through psql in the container.
    [Fact]
    public async Task Schema_invariants_hold()
    {
        const string database = "invariants";
        var connectionString = await postgres.CreateDatabaseAsync(database);
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());

        var script = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "invariants.sql"));
        await postgres.Container.CopyAsync(script, "/tmp/invariants.sql");
        var result = await postgres.Container.ExecAsync(
            ["psql", "-U", "postgres", "-d", database, "-v", "ON_ERROR_STOP=1", "-q", "-f", "/tmp/invariants.sql"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("ALL INVARIANT TESTS PASSED", result.Stdout);
    }
}
