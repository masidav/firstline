using FandomScore.Worker.AccountRetention;
using FandomScore.Worker.Hosting;
using FandomScore.Worker.ArtistOnboarding;
using FandomScore.Worker.Concerts;
using FandomScore.Worker.GrabBag;
using FandomScore.Worker.ImageRedaction;
using FandomScore.Spotify;
using FandomScore.Storage;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.Notifications;
using FandomScore.Worker.Scoring;
using FandomScore.Worker.SpotifySync;
using FandomScore.Worker.StorageCleanup;

var builder = Host.CreateApplicationBuilder(args);

// Secrets come from the environment (ConnectionStrings__Database), never from appsettings. In Kubernetes they are
// files named after the configuration key in Secrets__Directory (Secret Manager mount, infrastructure/k8s).
builder.Configuration.AddSecretsDirectory();
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
builder.Services.AddSpotify(builder.Configuration);
builder.Services.Configure<BaselineOptions>(builder.Configuration.GetSection(BaselineOptions.Section));
builder.Services.AddSingleton<CatalogLoader>();

// Stored files (Storage__LocalPath): must be the same storage the API writes to, or cleanup can't delete them.
builder.Services.AddFileStore(builder.Configuration, builder.Environment.ContentRootPath);

// Concert data (Ticketmaster__ApiKey; empty turns it off). workers/concerts.
builder.Services.Configure<TicketmasterOptions>(builder.Configuration.GetSection(TicketmasterOptions.Section));
builder.Services.AddHttpClient<TicketmasterClient>(http => http.Timeout = TicketmasterClient.RequestTimeout)
    .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

// One line per job kind. Feature folders under workers/ hold the handlers.
builder.Services.AddSingleton<IJobHandler, PruneJobsJob>();
builder.Services.AddSingleton<IJobHandler, PurgeDeletedAccountsJob>();
builder.Services.AddSingleton<IJobHandler, StorageCleanupJob>();
builder.Services.AddSingleton<IJobHandler, RedactSubmissionJob>();
builder.Services.AddSingleton<IJobHandler, RedactionSweepJob>();
builder.Services.AddSingleton<IJobHandler, SpotifySyncJob>();
builder.Services.AddSingleton<IJobHandler, ScheduleSpotifySyncsJob>();
builder.Services.AddSingleton<IJobHandler, PollPlaysJob>();
builder.Services.AddSingleton<IJobHandler, ArtistOnboardingJob>();
builder.Services.AddSingleton<IJobHandler, CatalogRefreshJob>();
builder.Services.AddSingleton<IJobHandler, ScheduleCatalogRefreshesJob>();
builder.Services.AddSingleton<IJobHandler, GrabBagRefreshJob>();
builder.Services.AddSingleton<IJobHandler, TicketDeliveredJob>();
builder.Services.AddSingleton<IJobHandler, ScoreArtistJob>();
builder.Services.AddSingleton<IJobHandler, RescoreAllJob>();
builder.Services.AddSingleton<IJobHandler, ConcertSyncJob>();
builder.Services.AddSingleton<IJobHandler, ScheduleConcertSyncsJob>();

builder.Build().Run();
