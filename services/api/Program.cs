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
using FandomScore.Api.Features.Moderation;
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

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<StandingSnapshots>();
builder.Services.AddAuth(builder.Configuration);
builder.Services.AddSpotify(builder.Configuration);
// Spotify access tokens for the player, held in memory only (ADR 0005).
builder.Services.AddMemoryCache();
builder.Services.AddScoped<SpotifyPlayerAccess>();
builder.Services.AddArtists();
builder.Services.AddDataExport();
builder.Services.AddUploads(builder.Configuration, builder.Environment);
builder.Services.AddApiProblems();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

// Lets the Expo web build call the API (Cors__AllowedOrigins).
builder.Services.AddWebClients(builder.Configuration);
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
v1.MapMe();
v1.MapSpotify();
v1.MapSpotifyPlayer();
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
