using FandomScore.Worker.AccountRetention;
using FandomScore.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);

// Secrets come from the environment (ConnectionStrings__Database), never from appsettings.
var connectionString = builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Database is not configured. See .env.example.");
}

builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<JobOptions>(builder.Configuration.GetSection(JobOptions.Section));
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<JobRunner>();

// One line per job kind. Feature folders under workers/ hold the handlers.
builder.Services.AddSingleton<IJobHandler, PruneJobsJob>();
builder.Services.AddSingleton<IJobHandler, PurgeDeletedAccountsJob>();

builder.Build().Run();
