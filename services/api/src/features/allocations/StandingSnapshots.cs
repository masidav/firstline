using System.Collections.Concurrent;
using Dapper;
using Npgsql;

namespace FandomScore.Api.Features.Allocations;

/// <summary>
/// The live registrations of an allocation, held in memory for a few seconds so a fan's standing ("where would I
/// place if the draw ran now") is a binary search instead of a count over every registration. During an on-sale
/// every fan refreshing the show page asks for their standing; counted in SQL that cost about 10 ms per request at
/// 20,000 registrants and grew with each one (tools/load/README.md), while registering costs a fraction of that.
///
/// Each pod refreshes an allocation's snapshot at most once per <c>Allocations:StandingSnapshotSeconds</c> (5 by
/// default; 0 reads fresh every time), one query at a time: requests that arrive while it runs wait for that query
/// instead of starting their own. Standing is a projection anyway, so a few seconds of lag only shifts it by the
/// fans who registered in that window.
/// </summary>
public sealed class StandingSnapshots(NpgsqlDataSource db, TimeProvider time, IConfiguration configuration)
{
    private static readonly TimeSpan Unused = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, Slot> _slots = new();

    private TimeSpan MaxAge => TimeSpan.FromSeconds(configuration.GetValue("Allocations:StandingSnapshotSeconds", 5));

    /// <summary>The allocation's live registrations, at most <see cref="MaxAge"/> old.</summary>
    public async Task<Snapshot> GetAsync(Guid allocationId)
    {
        var maxAge = MaxAge;
        if (maxAge <= TimeSpan.Zero)
        {
            return await LoadAsync(allocationId);
        }

        var now = time.GetUtcNow();
        var slot = _slots.GetOrAdd(allocationId, _ => new Slot());
        Task<Snapshot> task;
        lock (slot)
        {
            slot.LastUsed = now;
            if (slot.Task is null || (slot.Task.IsCompleted && (slot.Task.IsFaulted || now - slot.LoadedAt > maxAge)))
            {
                slot.LoadedAt = now;
                // Not tied to any one request: a cancelled request must not fail the others waiting on this load.
                slot.Task = LoadAsync(allocationId);
            }

            task = slot.Task;
        }

        ForgetUnused(now);
        return await task;
    }

    private void ForgetUnused(DateTimeOffset now)
    {
        foreach (var (id, slot) in _slots)
        {
            if (now - slot.LastUsed > Unused)
            {
                _slots.TryRemove(id, out _);
            }
        }
    }

    private async Task<Snapshot> LoadAsync(Guid allocationId)
    {
        await using var connection = await db.OpenConnectionAsync();
        var rows = await connection.QueryAsync<Entry>(
            """
            SELECT user_id AS UserId, hub_match IS NOT NULL AS Local, raw_score AS RawScore,
                   CASE WHEN plus_one THEN 2 ELSE 1 END AS Seats
            FROM allocation_registrations
            WHERE allocation_id = @allocationId AND withdrawn_at IS NULL
            """,
            new { allocationId });
        return new Snapshot(rows);
    }

    private sealed class Slot
    {
        public Task<Snapshot>? Task { get; set; }
        public DateTimeOffset LoadedAt { get; set; }
        public DateTimeOffset LastUsed { get; set; }
    }

    /// <summary>One live registration: whose, whether the show's city is in their hubs, their snapshot score, and seats asked for.</summary>
    public sealed record Entry(Guid UserId, bool Local, decimal RawScore, int Seats);

    /// <summary>Registrations grouped like the draw ranks them: local fans, then the rest, each by score.</summary>
    public sealed class Snapshot
    {
        private readonly Group _local;
        private readonly Group _others;
        private readonly Dictionary<Guid, Entry> _byUser;

        public Snapshot(IEnumerable<Entry> entries)
        {
            var list = entries.ToList();
            _byUser = list.ToDictionary(e => e.UserId);
            _local = new Group(list.Where(e => e.Local));
            _others = new Group(list.Where(e => !e.Local));
        }

        public int Count => _byUser.Count;

        /// <summary>
        /// Like the draw, but equal scores count as ahead (the draw breaks ties at random): how many other fans, and
        /// how many seats, would come before a fan with this place and score. The fan's own registration, if it is
        /// in the snapshot, doesn't count.
        /// </summary>
        public (int Others, int Ahead, int SeatsAhead) Rank(Guid userId, bool local, decimal rawScore)
        {
            var (ahead, seatsAhead) = local ? _local.AtLeast(rawScore) : _others.AtLeast(rawScore);
            if (!local)
            {
                // Every local fan comes before a fan from elsewhere.
                ahead += _local.Count;
                seatsAhead += _local.Seats;
            }

            var others = Count;
            if (_byUser.TryGetValue(userId, out var own))
            {
                others--;
                if (own.Local == local ? own.RawScore >= rawScore : own.Local)
                {
                    ahead--;
                    seatsAhead -= own.Seats;
                }
            }

            return (others, ahead, seatsAhead);
        }

        private sealed class Group
        {
            // Scores highest first, with running totals of fans and seats down to each position.
            private readonly decimal[] _scores;
            private readonly int[] _seatsThrough;

            public Group(IEnumerable<Entry> entries)
            {
                var sorted = entries.OrderByDescending(e => e.RawScore).ToArray();
                _scores = sorted.Select(e => e.RawScore).ToArray();
                _seatsThrough = new int[sorted.Length];
                var seats = 0;
                for (var i = 0; i < sorted.Length; i++)
                {
                    seats += sorted[i].Seats;
                    _seatsThrough[i] = seats;
                }
            }

            public int Count => _scores.Length;

            public int Seats => _seatsThrough.Length == 0 ? 0 : _seatsThrough[^1];

            /// <summary>Fans scoring at least <paramref name="score"/>, and their seats.</summary>
            public (int Count, int Seats) AtLeast(decimal score)
            {
                // First index whose score is below the given one; everything before it is at least as high.
                int low = 0, high = _scores.Length;
                while (low < high)
                {
                    var mid = (low + high) / 2;
                    if (_scores[mid] >= score)
                    {
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid;
                    }
                }

                return (low, low == 0 ? 0 : _seatsThrough[low - 1]);
            }
        }
    }
}
