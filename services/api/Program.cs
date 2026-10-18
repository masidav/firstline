using System.Text.Json;
using FandomScore.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Secrets come from the environment (ConnectionStrings__Database), never from appsettings.
var connectionString = builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Database is not configured. See .env.example.");
}

builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddApiProblems();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

var v1 = app.MapGroup("/v1");
v1.MapGet("/health", () => Results.Ok(new { Status = "ok" }));

app.Run();

// Exposed for WebApplicationFactory in tests.
public partial class Program;
