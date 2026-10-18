using FandomScore.Database;

// Usage: dotnet run --project database  (reads ConnectionStrings__Database)
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set ConnectionStrings__Database to the target PostgreSQL connection string.");
    return 2;
}

var result = Migrator.Upgrade(connectionString);
if (!result.Successful)
{
    Console.Error.WriteLine($"Migration failed: {result.Error.Message}");
    return 1;
}

Console.WriteLine($"Database is up to date ({result.Scripts.Count()} script(s) applied).");
return 0;
