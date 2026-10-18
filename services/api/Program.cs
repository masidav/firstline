using System.Text.Json;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Users;
using FandomScore.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Secrets come from the environment (ConnectionStrings__Database), never from appsettings.
var connectionString = builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Database is not configured. See .env.example.");
}

builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAuth(builder.Configuration);
builder.Services.AddApiProblems();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Every API endpoint needs a signed-in user unless it says AllowAnonymous (openapi global security).
var v1 = app.MapGroup("/v1").RequireAuthorization();
v1.MapGet("/health", () => Results.Ok(new { Status = "ok" })).AllowAnonymous();
v1.MapAuth();
v1.MapAppleNotifications();
v1.MapMe();

app.Run();

// Exposed for WebApplicationFactory in tests.
public partial class Program;
