using Npgsql;
using Testcontainers.PostgreSql;

namespace FandomScore.Tests;

/// <summary>One disposable PostgreSQL server per test run; each test creates its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:16").Build();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(Container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(Container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
