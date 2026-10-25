using Dapper;
using FandomScore.Database;
using FandomScore.Spotify;
using FandomScore.Worker.GrabBag;
using FandomScore.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FandomScore.Tests;

/// <summary>The daily grab-bag song refresh (migration 0019) against a real database, with Spotify faked.</summary>
[Collection(PostgresCollection.Name)]
public sealed class GrabBagRefreshJobTests(PostgresFixture postgres) : IAsyncLifetime, IDisposable
{
    private const string Stale = "1aaaaaaaaaaaaaaaaaaaaa";
    private const string Fresh = "2aaaaaaaaaaaaaaaaaaaaa";
    private const string Gone = "3aaaaaaaaaaaaaaaaaaaaa";

    private readonly FakeSpotify _spotify = new();
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync($"grab_bag_refresh_{Guid.NewGuid():N}");
        var migration = Migrator.Upgrade(connectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
        _db = NpgsqlDataSource.Create(connectionString);

        var artistId = await ScoringSetup.ActiveArtistAsync(_db, "sp-geese", "Geese", releases: 1);
        _spotify.Tracks.Add(new FakeSpotify.TrackDetail(Stale, "Cowboy Nudes (Remastered)", "al-new", "3D Country (Deluxe)", ("sp-geese", "Geese")));
        _spotify.Tracks.Add(new FakeSpotify.TrackDetail(Fresh, "2122 (Live)", "al-live", "Live", ("sp-geese", "Geese")));
        await using var connection = await _db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO users (handle, email) VALUES ('ana', 'ana@example.test');
            INSERT INTO grab_bag_songs (user_id, position, spotify_track_id, title, artist_id, album_title, added_at, refreshed_at)
            SELECT id, 1, @Stale, 'Cowboy Nudes', @artistId, '3D Country', now() - interval '30 days', now() - interval '8 days' FROM users
            UNION ALL SELECT id, 2, @Fresh, '2122', @artistId, '3D Country', now() - interval '2 days', NULL FROM users
            UNION ALL SELECT id, 3, @Gone, 'Lost Song', @artistId, 'Lost', now() - interval '9 days', NULL FROM users;
            """,
            new { Stale, Fresh, Gone, artistId });
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    public void Dispose() => _spotify.Dispose();

    [Fact]
    public async Task Stale_songs_are_re_read_from_Spotify_fresh_ones_are_left_alone_and_gone_ones_are_stamped()
    {
        var options = FakeSpotify.Options();
        options.ClientSecret = "test-client-secret";
        var job = new GrabBagRefreshJob(_db, _spotify.Client(options), Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<GrabBagRefreshJob>.Instance);

        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);

        await using var connection = await _db.OpenConnectionAsync();
        var songs = (await connection.QueryAsync<(string Id, string Title, string Album, string? Cover, bool Refreshed)>(
                """
                SELECT spotify_track_id, title, album_title, cover_url, refreshed_at > now() - interval '1 minute'
                FROM grab_bag_songs ORDER BY position
                """))
            .ToList();
        Assert.Equal((Stale, "Cowboy Nudes (Remastered)", "3D Country (Deluxe)", "https://i.scdn.co/al-new-640", true), songs[0]);
        Assert.Equal((Fresh, "2122", "3D Country", null, false), songs[1]);
        Assert.Equal((Gone, "Lost Song", "Lost", null, true), songs[2]);
        Assert.Equal(2, _spotify.Calls.Count(c => c.Path.StartsWith("tracks/", StringComparison.Ordinal)));
        Assert.Equal(TimeSpan.FromDays(1), job.Every);

        // Nothing is due any more: the next run calls Spotify for nothing.
        await job.RunAsync(new ClaimedJob(2, job.Kind, "{}", 1, 10), CancellationToken.None);
        Assert.Equal(2, _spotify.Calls.Count(c => c.Path.StartsWith("tracks/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_a_client_secret_the_job_does_nothing()
    {
        var options = FakeSpotify.Options();
        var job = new GrabBagRefreshJob(_db, _spotify.Client(options), Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<GrabBagRefreshJob>.Instance);

        await job.RunAsync(new ClaimedJob(1, job.Kind, "{}", 1, 10), CancellationToken.None);

        Assert.Empty(_spotify.Calls);
    }
}
