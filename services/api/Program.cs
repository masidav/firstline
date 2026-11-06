using System.Text.Json;
using FandomScore.Api.Features.AlbumJournal;
using FandomScore.Api.Features.Allocations;
using FandomScore.Api.Features.Artists;
using FandomScore.Api.Features.Authentication;
using FandomScore.Api.Features.Concerts;
using FandomScore.Api.Features.Featured;
using FandomScore.Api.Features.FanLevels;
using FandomScore.Api.Features.Feed;
using FandomScore.Api.Features.Follows;
using FandomScore.Api.Features.Links;
using FandomScore.Api.Features.GrabBag;
using FandomScore.Api.Features.Fraud;
using FandomScore.Api.Features.Moderation;
using FandomScore.Api.Features.MusicSync;
using FandomScore.MusicProviders;
using FandomScore.Api.Features.Notifications;
using FandomScore.Api.Features.Organizations;
using FandomScore.Api.Features.Privacy;
using FandomScore.Api.Features.Prompts;
using FandomScore.Api.Features.Reactions;
using FandomScore.Api.Features.Redaction;
using FandomScore.Api.Features.Scoring;
using FandomScore.Api.Features.Sharing;
using FandomScore.Api.Features.SpotifyPlayer;
using FandomScore.Api.Features.SpotifySync;
using FandomScore.Api.Features.Stats;
using FandomScore.Api.Features.Submissions;
using FandomScore.Api.Features.Trivia;
using FandomScore.Api.Features.Users;
using FandomScore.Api.Infrastructure;
using FandomScore.Spotify;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Secrets come from the environment (ConnectionStrings__Database), never from appsettings. In Kubernetes they are
// files named after the configuration key in Secrets__Directory (Secret Manager mount, infrastructure/k8s).
builder.Configuration.AddSecretsDirectory();
var connectionString = builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Database is not configured. See .env.example.");
}

// Two settings from the on-sale load test (tools/load/README.md, "Full HTTP path"), where parsing and planning, not
// running, was most of the database's CPU:
// - Automatic preparation: a statement run often on a connection is prepared there, so it is planned once instead of
//   on every request. At 200 fans a second it took the database from 1.2 cores to 0.4. Every API query names its
//   columns, so a migration can't change a prepared statement's result type under a running pod.
// - No reset on close: otherwise every connection returned to the pool runs DISCARD ALL, which also throws away the
//   plans PL/pgSQL caches per connection, so registration_blocker() and register_for_allocation() were planned again
//   on every request (3x their cost). The API keeps no session state on a connection: SET LOCAL and
//   set_config(..., true) end with the transaction, and the one session advisory lock (SpotifyPlayerAccess) is
//   released in a finally block.
builder.Services.AddNpgsqlDataSource(connectionString, source =>
{
    source.ConnectionStringBuilder.NoResetOnClose = true;
    if (source.ConnectionStringBuilder.MaxAutoPrepare == 0)
    {
        source.ConnectionStringBuilder.MaxAutoPrepare = 100;
    }
});
// The standing snapshot has its own small pool. Requests that show a standing hold a connection from the main pool
// while they wait for the snapshot, so loading it from the same pool deadlocked under load: every connection held by
// a request waiting for a load that was itself waiting for a connection, until the pool timeout failed them all.
builder.Services.AddNpgsqlDataSource(connectionString, source => source.ConnectionStringBuilder.MaxPoolSize = 4,
    serviceKey: StandingSnapshots.DataSourceKey);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new StandingSnapshots(
    sp.GetRequiredKeyedService<NpgsqlDataSource>(StandingSnapshots.DataSourceKey), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IConfiguration>()));
builder.Services.AddAuth(builder.Configuration);
builder.Services.AddSpotify(builder.Configuration);
// Apple Music and Tidal (ADR 0007); stored tokens share Spotify__TokenKey.
builder.Services.AddMusicProviders(builder.Configuration);
// Spotify access tokens for the player, held in memory only (ADR 0005).
builder.Services.AddMemoryCache();
builder.Services.AddScoped<SpotifyPlayerAccess>();
builder.Services.AddArtists();
builder.Services.AddDataExport();
builder.Services.AddAbuseLimits();
// Request bodies: 1 MB unless an endpoint raises it for itself (uploads do).
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = AbuseLimits.MaxBodyBytes);
builder.Services.AddUploads(builder.Configuration, builder.Environment);
builder.Services.AddApiProblems();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

// Lets the Expo web build call the API (Cors__AllowedOrigins).
builder.Services.AddWebClients(builder.Configuration);
// Absolute links start with App__PublicOrigin, not the request's Host header (required outside Development).
builder.Services.AddPublicOrigin(builder.Configuration);
// Real client addresses behind the load balancer (ForwardedHeaders__KnownProxies / KnownNetworks).
builder.Services.AddLoadBalancerForwarding(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseCors();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Every API endpoint needs a signed-in user unless it says AllowAnonymous (openapi global security).
var v1 = app.MapGroup("/v1").RequireAuthorization();
v1.MapGet("/health", () => Results.Ok(new { Status = "ok" })).AllowAnonymous();
v1.MapAuth();
v1.MapAppleNotifications();
v1.MapAppleWeb();
v1.MapMe();
v1.MapAvatars();
v1.MapSpotify();
v1.MapSpotifyPlayer();
v1.MapSpotifyLibrary();
v1.MapMusic();
v1.MapArtists();
v1.MapAllocations();
v1.MapOrganizations();
v1.MapAlbums();
v1.MapJournal();
v1.MapScoring();
v1.MapStats();
v1.MapUploads();
v1.MapSubmissions();
v1.MapFeed();
v1.MapFollows();
v1.MapGrabBag();
v1.MapReports();
v1.MapFraud();
v1.MapLocationChecks();
v1.MapNotifications();
v1.MapPushDevices();
v1.MapTrivia();
v1.MapFeatured();
v1.MapPrompts();
v1.MapReactions();
v1.MapRedaction();
v1.MapConcertSources();
v1.MapScoreCards();
v1.MapLinks();
v1.MapDataExport();
v1.MapFanLevels();

app.Run();

// Exposed for WebApplicationFactory in tests.
public partial class Program;
