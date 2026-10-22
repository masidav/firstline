using System.Net;
using System.Text.Json;
using Dapper;
using FandomScore.Database;
using FandomScore.Spotify;
using FandomScore.Worker.ArtistOnboarding;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>Artist onboarding, catalog refresh, and its schedule against a real database, with Spotify faked (ADR 0006).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ArtistOnboardingJobTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private const string Geese = "sp-geese";
    private const string Other = "sp-other";

    private readonly FakeSpotify _spotify = new();
    private readonly SpotifyTokenProtector _protector = new(FakeSpotify.Options());
    private NpgsqlDataSource _db = null!;
    private Guid _userId;
    private Guid _artistId;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"onboarding_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        await using var connection = await _db.OpenConnectionAsync();
        _userId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (handle, email) VALUES ('fan_one', 'fan@example.test') RETURNING id");
        await connection.ExecuteAsync(
            "INSERT INTO spotify_connections (user_id, spotify_user_id, refresh_token_enc, scopes) VALUES (@_userId, 'sp-user-1', @token, @scopes)",
            new { _userId, token = _protector.Protect(_userId, "refresh-0"), scopes = SpotifyOptions.RequiredScopes.ToArray() });
        // As POST /artists does: the row and its onboarding job in one transaction.
        _artistId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT o_artist_id FROM track_artist(@_userId, @Geese, 'Geese')", new { _userId, Geese });

        _spotify.ArtistAlbums[Geese] =
        [
            new FakeSpotify.Album("al-3d", "3D Country", "album", "2023-06-23", "day", Geese),
            new FakeSpotify.Album("al-single", "Cowboy Nudes", "single", "2023", "year", Geese),
            new FakeSpotify.Album("al-feat", "Someone Else's LP", "album", "2022-01", "month", Other, Geese),
            new FakeSpotify.Album("al-comp", "Various", "appears_on", "2021-01-01", "day", Other),
        ];
        _spotify.AlbumTracks["al-3d"] =
        [
            FakeSpotify.AlbumTrack("tr-2122", "2122", 1),
            FakeSpotify.AlbumTrack("tr-3d", "3D Country", 2),
            FakeSpotify.AlbumTrack("tr-bonus", "Bonus", 1, discNumber: 2),
        ];
        _spotify.AlbumTracks["al-single"] = [FakeSpotify.AlbumTrack("tr-cowboy", "Cowboy Nudes", 1)];
        _spotify.AlbumTracks["al-feat"] = [FakeSpotify.AlbumTrack("tr-feat", "Feature", 1)];
        _spotify.AlbumTracks["al-comp"] = [FakeSpotify.AlbumTrack("tr-comp", "Comp", 1)];
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    public void Dispose() => _spotify.Dispose();

    [Fact]
    public async Task Onboarding_loads_the_catalog_activates_the_artist_and_syncs_the_fan_who_added_it()
    {
        Assert.True(await RunQueuedAsync());

        var artist = await ArtistAsync();
        Assert.Equal("active", artist.Status);
        Assert.NotNull(artist.ActivatedAt);
        Assert.NotNull(artist.CatalogLoadedAt);

        // Own releases only: the album credited first to another artist and the appears_on compilation are skipped.
        var albums = await AlbumsAsync();
        Assert.Equal(["al-3d", "al-single"], albums.Select(a => a.SpotifyAlbumId));
        var lp = albums[0];
        Assert.Equal(("3D Country", "album", new DateOnly(2023, 6, 23), "day", "https://i.scdn.co/al-3d-640"),
            (lp.Title, lp.AlbumType, lp.ReleaseDate, lp.ReleaseDatePrecision, lp.CoverUrl));
        var single = albums[1];
        Assert.Equal(("Cowboy Nudes", "single", new DateOnly(2023, 1, 1), "year"),
            (single.Title, single.AlbumType, single.ReleaseDate, single.ReleaseDatePrecision));
        Assert.All(albums, a => Assert.Null(a.DelistedAt));

        var tracks = await TracksAsync();
        Assert.Equal(
            [("al-3d", "tr-2122", 1, 1), ("al-3d", "tr-3d", 1, 2), ("al-3d", "tr-bonus", 2, 1), ("al-single", "tr-cowboy", 1, 1)],
            tracks.Select(t => (t.SpotifyAlbumId, t.SpotifyTrackId, (int)t.DiscNumber, (int)t.TrackNumber!)));

        await using var connection = await _db.OpenConnectionAsync();
        var baseline = await connection.QuerySingleAsync<(string Family, string Params, string Proxy, string Curve, int BlendK)>(
            "SELECT dist_family, dist_params::text, proxy_metrics::text, curve::text, blend_k FROM artist_baselines WHERE artist_id = @_artistId",
            new { _artistId });
        Assert.Equal("lognormal", baseline.Family);
        using var distParams = JsonDocument.Parse(baseline.Params);
        Assert.True(distParams.RootElement.GetProperty("provisional").GetBoolean());
        using var proxy = JsonDocument.Parse(baseline.Proxy);
        Assert.Equal((2, 4), (proxy.RootElement.GetProperty("albums").GetInt32(), proxy.RootElement.GetProperty("tracks").GetInt32()));
        using var curve = JsonDocument.Parse(baseline.Curve);
        Assert.Equal(61, curve.RootElement.GetArrayLength());
        Assert.Equal(200, baseline.BlendK);

        // The fan who added the artist gets a sync; the onboarding job itself succeeded.
        Assert.Equal(["artist.onboard:succeeded", "spotify.sync:queued"], await JobsAsync());

        var albumsCall = Assert.Single(_spotify.Calls, c => c.Path == $"artists/{Geese}/albums");
        Assert.Equal("app-1", albumsCall.Bearer);
        Assert.Contains("include_groups=album,single,compilation", albumsCall.Query, StringComparison.Ordinal);
        Assert.Contains("market=US", albumsCall.Query, StringComparison.Ordinal);
        Assert.Contains("limit=10", albumsCall.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(_spotify.Calls, c => c.Path is "albums/al-feat/tracks" or "albums/al-comp/tracks");
    }

    [Fact]
    public async Task Onboarding_leaves_an_artist_that_is_no_longer_onboarding_alone()
    {
        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO artist_baselines (artist_id, dist_params, curve) VALUES (@_artistId, '{}', '[]');
                UPDATE artists SET status = 'active', catalog_loaded_at = now() WHERE id = @_artistId;
                """,
                new { _artistId });
        }

        Assert.True(await RunQueuedAsync());

        Assert.Empty(_spotify.Calls);
        Assert.Empty(await AlbumsAsync());
        Assert.Equal(["artist.onboard:succeeded"], await JobsAsync());
    }

    [Fact]
    public async Task A_spotify_outage_leaves_the_artist_onboarding_and_the_job_retries()
    {
        _spotify.ApiStatus = HttpStatusCode.ServiceUnavailable;

        Assert.True(await RunQueuedAsync());

        Assert.Equal("onboarding", (await ArtistAsync()).Status);
        Assert.Empty(await AlbumsAsync());
        var job = await JobAsync("artist.onboard");
        Assert.Equal(("queued", 1), (job.Status, job.Attempts));
        Assert.Contains("SpotifyUnavailableException", job.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_client_secret_the_job_retries_until_the_server_is_configured()
    {
        Assert.True(await RunQueuedAsync(FakeSpotify.Options()));

        Assert.Equal("onboarding", (await ArtistAsync()).Status);
        Assert.Empty(_spotify.Calls);
        var job = await JobAsync("artist.onboard");
        Assert.Equal("queued", job.Status);
        Assert.Contains("Spotify:ClientSecret", job.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_artist_spotify_does_not_know_is_paused()
    {
        _spotify.ArtistAlbums.Remove(Geese);

        Assert.True(await RunQueuedAsync());

        Assert.Equal("paused", (await ArtistAsync()).Status);
        Assert.Equal(["artist.onboard:succeeded"], await JobsAsync());
    }

    [Fact]
    public async Task The_album_cap_keeps_the_first_releases_spotify_lists()
    {
        Assert.True(await RunQueuedAsync(Options(maxAlbums: 1)));

        Assert.Equal(["al-3d"], (await AlbumsAsync()).Select(a => a.SpotifyAlbumId));
        Assert.Equal("active", (await ArtistAsync()).Status);
    }

    [Fact]
    public async Task Refresh_updates_titles_delists_removed_releases_and_restores_them_when_they_return()
    {
        Assert.True(await RunQueuedAsync());
        Guid logId;
        await using (var connection = await _db.OpenConnectionAsync())
        {
            logId = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO album_logs (user_id, album_id) SELECT @_userId, id FROM albums WHERE spotify_album_id = 'al-single' RETURNING id",
                new { _userId });
        }

        // Spotify renames the LP, drops its second track, and pulls the single.
        _spotify.ArtistAlbums[Geese][0] = _spotify.ArtistAlbums[Geese][0] with { Name = "3D Country (Deluxe)" };
        _spotify.ArtistAlbums[Geese].RemoveAt(1);
        _spotify.AlbumTracks["al-3d"].RemoveAt(1);
        await RefreshAsync();

        var albums = await AlbumsAsync();
        Assert.Equal("3D Country (Deluxe)", albums.Single(a => a.SpotifyAlbumId == "al-3d").Title);
        Assert.NotNull(albums.Single(a => a.SpotifyAlbumId == "al-single").DelistedAt);
        Assert.Equal(["tr-3d", "tr-cowboy"], (await TracksAsync()).Where(t => t.DelistedAt is not null).Select(t => t.SpotifyTrackId));
        await using (var connection = await _db.OpenConnectionAsync())
        {
            Assert.True(await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM album_logs WHERE id = @logId AND deleted_at IS NULL)", new { logId }));
        }

        // The single comes back.
        _spotify.ArtistAlbums[Geese].Add(new FakeSpotify.Album("al-single", "Cowboy Nudes", "single", "2023", "year", Geese));
        await RefreshAsync();

        Assert.Null((await AlbumsAsync()).Single(a => a.SpotifyAlbumId == "al-single").DelistedAt);
        Assert.Equal(["tr-3d"], (await TracksAsync()).Where(t => t.DelistedAt is not null).Select(t => t.SpotifyTrackId));
    }

    [Fact]
    public async Task Refresh_pauses_an_artist_spotify_no_longer_has()
    {
        Assert.True(await RunQueuedAsync());
        _spotify.ArtistAlbums.Remove(Geese);

        await RefreshAsync();

        Assert.Equal("paused", (await ArtistAsync()).Status);
        Assert.All(await AlbumsAsync(), a => Assert.Null(a.DelistedAt));
    }

    [Fact]
    public async Task The_schedule_queues_one_refresh_per_stale_active_artist()
    {
        Assert.True(await RunQueuedAsync());
        var schedule = new ScheduleCatalogRefreshesJob(_db, Microsoft.Extensions.Options.Options.Create(Options()),
            NullLogger<ScheduleCatalogRefreshesJob>.Instance);
        var job = new ClaimedJob(99, schedule.Kind, "{}", 1, 10);

        await schedule.RunAsync(job, CancellationToken.None);
        Assert.DoesNotContain("catalog.refresh:queued", await JobsAsync());   // loaded just now

        await using (var connection = await _db.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE artists SET catalog_loaded_at = now() - interval '8 days' WHERE id = @_artistId", new { _artistId });
        }

        await schedule.RunAsync(job, CancellationToken.None);
        await schedule.RunAsync(job, CancellationToken.None);
        Assert.Equal(1, (await JobsAsync()).Count(j => j == "catalog.refresh:queued"));
    }

    [Theory]
    [InlineData("2023-06-23", "day", 2023, 6, 23, "day")]
    [InlineData("2023-06", "month", 2023, 6, 1, "month")]
    [InlineData("1981", "year", 1981, 1, 1, "year")]
    [InlineData("1981", null, 1981, 1, 1, "year")]
    public void Release_dates_keep_their_precision(string raw, string? precision, int year, int month, int day, string expected)
    {
        Assert.Equal((new DateOnly(year, month, day), expected), CatalogLoader.ParseReleaseDate(raw, precision));
    }

    [Fact]
    public void Unparseable_release_dates_are_stored_as_null()
    {
        Assert.Equal((null, "day"), CatalogLoader.ParseReleaseDate("0000", "year"));
        Assert.Equal((null, "day"), CatalogLoader.ParseReleaseDate(null, null));
    }

    [Fact]
    public void The_provisional_baseline_is_a_lognormal_curve_around_the_median()
    {
        var baseline = ProvisionalBaseline.Create(new BaselineOptions { MedianScore = 5, Sigma = 1, BlendK = 200 },
            new CatalogLoad(3, 30, 0), DateTimeOffset.UnixEpoch);

        var curve = JsonSerializer.Deserialize<double[][]>(baseline.Curve)!;
        var median = curve.Single(p => Math.Abs(p[1] - 0.5) < 1e-9);
        Assert.Equal(5, median[0]);
        Assert.True(curve.Zip(curve.Skip(1)).All(pair => pair.First[0] < pair.Second[0] && pair.First[1] < pair.Second[1]));
        Assert.InRange(ProvisionalBaseline.NormalCdf(1.96), 0.9749, 0.9751);
    }

    private static SpotifyOptions Options(int maxAlbums = 500)
    {
        var options = FakeSpotify.Options();
        options.ClientSecret = "test-client-secret";
        options.MaxCatalogAlbums = maxAlbums;
        return options;
    }

    /// <summary>Runs the next queued job (the onboarding one) through the runner, as the worker would.</summary>
    private async Task<bool> RunQueuedAsync(SpotifyOptions? options = null)
    {
        var spotifyOptions = Microsoft.Extensions.Options.Options.Create(options ?? Options());
        var loader = new CatalogLoader(_spotify.Client(spotifyOptions.Value), spotifyOptions, NullLogger<CatalogLoader>.Instance);
        var jobOptions = Microsoft.Extensions.Options.Options.Create(new JobOptions());
        var runner = new JobRunner(new JobQueue(_db, jobOptions),
            [new ArtistOnboardingJob(_db, loader, spotifyOptions, Microsoft.Extensions.Options.Options.Create(new BaselineOptions()),
                TimeProvider.System, NullLogger<ArtistOnboardingJob>.Instance)],
            jobOptions, TimeProvider.System, NullLogger<JobRunner>.Instance);
        return await runner.RunOnceAsync(CancellationToken.None);
    }

    private async Task RefreshAsync()
    {
        var spotifyOptions = Microsoft.Extensions.Options.Options.Create(Options());
        var loader = new CatalogLoader(_spotify.Client(spotifyOptions.Value), spotifyOptions, NullLogger<CatalogLoader>.Instance);
        var job = new CatalogRefreshJob(_db, loader, spotifyOptions, NullLogger<CatalogRefreshJob>.Instance);
        await job.RunAsync(new ClaimedJob(2, CatalogRefreshJob.JobKind, JsonSerializer.Serialize(new { artist_id = _artistId }), 1, 10),
            CancellationToken.None);
    }

    private sealed record ArtistRow(string Status, DateTime? ActivatedAt, DateTime? CatalogLoadedAt);

    private async Task<ArtistRow> ArtistAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QuerySingleAsync<ArtistRow>(
            "SELECT status::text AS Status, activated_at AS ActivatedAt, catalog_loaded_at AS CatalogLoadedAt FROM artists WHERE id = @_artistId",
            new { _artistId });
    }

    private sealed record AlbumRow(
        string SpotifyAlbumId, string Title, string AlbumType, DateOnly? ReleaseDate, string ReleaseDatePrecision, string? CoverUrl,
        DateTime? DelistedAt);

    private async Task<List<AlbumRow>> AlbumsAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<AlbumRow>(
            """
            SELECT spotify_album_id AS SpotifyAlbumId, title AS Title, album_type AS AlbumType, release_date AS ReleaseDate,
                   release_date_precision AS ReleaseDatePrecision, cover_url AS CoverUrl, delisted_at AS DelistedAt
            FROM albums WHERE artist_id = @_artistId ORDER BY release_date DESC, spotify_album_id
            """,
            new { _artistId })];
    }

    private sealed record TrackRow(string SpotifyAlbumId, string SpotifyTrackId, short DiscNumber, short? TrackNumber, DateTime? DelistedAt);

    private async Task<List<TrackRow>> TracksAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<TrackRow>(
            """
            SELECT a.spotify_album_id AS SpotifyAlbumId, t.spotify_track_id AS SpotifyTrackId, t.disc_number AS DiscNumber,
                   t.track_number AS TrackNumber, t.delisted_at AS DelistedAt
            FROM tracks t JOIN albums a ON a.id = t.album_id
            WHERE a.artist_id = @_artistId ORDER BY a.release_date DESC, t.disc_number, t.track_number
            """,
            new { _artistId })];
    }

    private sealed record JobRow(string Status, int Attempts, string? LastError);

    private async Task<JobRow> JobAsync(string kind)
    {
        await using var connection = await _db.OpenConnectionAsync();
        return await connection.QuerySingleAsync<JobRow>(
            "SELECT status AS Status, attempts AS Attempts, last_error AS LastError FROM jobs WHERE kind = @kind", new { kind });
    }

    private async Task<List<string>> JobsAsync()
    {
        await using var connection = await _db.OpenConnectionAsync();
        return [.. await connection.QueryAsync<string>("SELECT kind || ':' || status FROM jobs ORDER BY kind")];
    }
}
