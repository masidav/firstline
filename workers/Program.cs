using FandomScore.Worker.AccountRetention;
using FandomScore.Worker.Allocations;
using FandomScore.Worker.Hosting;
using FandomScore.Worker.ArtistOnboarding;
using FandomScore.Worker.Concerts;
using FandomScore.Worker.Fans;
using FandomScore.Worker.Fraud;
using FandomScore.Worker.GrabBag;
using FandomScore.Worker.ImageRedaction;
using FandomScore.MusicProviders;
using FandomScore.Spotify;
using FandomScore.Storage;
using FandomScore.Worker.MusicSync;
using FandomScore.Worker.Jobs;
using FandomScore.Worker.Notifications;
using FandomScore.Worker.Notifications.Email;
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

// Liveness for Kubernetes (Health__Port; off locally): the job loops report each iteration, GET /healthz answers.
builder.Services.Configure<HealthOptions>(builder.Configuration.GetSection(HealthOptions.Section));
builder.Services.AddSingleton<WorkerHeartbeat>();
builder.Services.AddSingleton<IntegrationChecks>();
builder.Services.AddHostedService<WorkerHealthServer>();
builder.Services.AddSpotify(builder.Configuration);
builder.Services.AddMusicProviders(builder.Configuration);
builder.Services.Configure<BaselineOptions>(builder.Configuration.GetSection(BaselineOptions.Section));
builder.Services.AddSingleton<CatalogLoader>();

// Printed order numbers on proof photos (Redaction__TesseractCommand; empty keeps OCR off). workers/image-redaction.
builder.Services.Configure<RedactionOptions>(builder.Configuration.GetSection(RedactionOptions.Section));
builder.Services.AddSingleton<ITextFinder, TesseractCliTextFinder>();

// Stored files (Storage__LocalPath): must be the same storage the API writes to, or cleanup can't delete them.
builder.Services.AddFileStore(builder.Configuration, builder.Environment.ContentRootPath);

// Concert data (Ticketmaster__ApiKey; empty turns it off). workers/concerts.
builder.Services.Configure<TicketmasterOptions>(builder.Configuration.GetSection(TicketmasterOptions.Section));
builder.Services.AddHttpClient<TicketmasterClient>(http => http.Timeout = TicketmasterClient.RequestTimeout)
    .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddTransient<IIntegrationCheck, TicketmasterCheck>();   // GET /healthz/integrations (deploy.sh)

// Push notifications through Expo (Push__Enabled, Push__ExpoAccessToken). workers/notifications.
builder.Services.Configure<PushOptions>(builder.Configuration.GetSection(PushOptions.Section));
builder.Services.AddHttpClient<ExpoPushClient>(http => http.Timeout = ExpoPushClient.RequestTimeout)
    .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15))
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

// Staff notices by email (Email__Enabled, Email__Provider, Email__From, Email__AppOrigin). workers/notifications/email.
builder.Services.AddEmail(builder.Configuration);

// Retention periods still to be decided (Retention__*; unset keeps the record). workers/account-retention.
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.Section));

// One line per job kind. Feature folders under workers/ hold the handlers.
builder.Services.AddSingleton<IJobHandler, PruneJobsJob>();
builder.Services.AddSingleton<IJobHandler, PurgeDeletedAccountsJob>();
builder.Services.AddSingleton<IJobHandler, PruneExpiredRecordsJob>();
builder.Services.AddSingleton<IJobHandler, StorageCleanupJob>();
builder.Services.AddSingleton<IJobHandler, RedactSubmissionJob>();
builder.Services.AddSingleton<IJobHandler, RedactionSweepJob>();
builder.Services.AddSingleton<IJobHandler, SpotifySyncJob>();
builder.Services.AddSingleton<IJobHandler, ScheduleSpotifySyncsJob>();
builder.Services.AddSingleton<IJobHandler, PollPlaysJob>();
builder.Services.AddSingleton<IJobHandler, MusicSyncJob>();
builder.Services.AddSingleton<IJobHandler, ScheduleMusicSyncsJob>();
builder.Services.AddSingleton<IJobHandler, ArtistOnboardingJob>();
builder.Services.AddSingleton<IJobHandler, CatalogRefreshJob>();
builder.Services.AddSingleton<IJobHandler, ScheduleCatalogRefreshesJob>();
builder.Services.AddSingleton<IJobHandler, GrabBagRefreshJob>();
builder.Services.AddSingleton<IJobHandler, TicketDeliveredJob>();
builder.Services.AddSingleton<IJobHandler, SelectAllocationJob>();
builder.Services.AddSingleton<IJobHandler, PresaleReminderJob>();
builder.Services.AddSingleton<IJobHandler, FanScoresJob>();
builder.Services.AddSingleton<IJobHandler, DetectFraudJob>();
builder.Services.AddSingleton<IJobHandler, PushNotificationJob>();
builder.Services.AddSingleton<IJobHandler, PushReceiptsJob>();
builder.Services.AddSingleton<IJobHandler, StaffEmailJob>();
builder.Services.AddSingleton<IJobHandler, ScoreArtistJob>();
builder.Services.AddSingleton<IJobHandler, RescoreAllJob>();
builder.Services.AddSingleton<IJobHandler, ConcertSyncJob>();
builder.Services.AddSingleton<IJobHandler, ScheduleConcertSyncsJob>();

builder.Build().Run();
