using FandomScore.Database;
using Microsoft.Extensions.Configuration;

// Usage: dotnet run --project database  (reads ConnectionStrings__Database from the environment, or from a file
// of that name in Secrets__Directory when run as the Kubernetes migrate Job; see infrastructure/k8s).
var configuration = new ConfigurationBuilder().AddEnvironmentVariables();
var secretsDirectory = Environment.GetEnvironmentVariable("Secrets__Directory");
if (!string.IsNullOrWhiteSpace(secretsDirectory))
{
    configuration.AddKeyPerFile(secretsDirectory, optional: true, reloadOnChange: false);
}

var connectionString = configuration.Build().GetConnectionString("Database");
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
