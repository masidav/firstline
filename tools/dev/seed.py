#!/usr/bin/env python3
"""Fill the LOCAL development database (dev, 127.0.0.1) with fake fans, artists, shows, allocations, and registrations.

Usage, from the repo root with the compose database running and migrations applied (docs/DEVELOPMENT.md):
  python3 tools/dev/seed.py                          # 200 fans, 8 artists, 10 shows
  python3 tools/dev/seed.py --fans 1000 --seed 7     # more fans, a different deterministic data set
  python3 tools/dev/seed.py --wipe                   # wipe the local database first (testScripts/wipeLocalDb.sh asks)
  python3 tools/dev/seed.py --sql | psql ...         # print the SQL instead of running it (CI does this)

What you get, all deterministic for a given --seed:
  - fans with hub cities, Spotify-like signals, album journal logs (which award ledger points through the real
    triggers), a few verified ticket and merch submissions, and a fandom score per followed artist;
  - active artists ('seed:...' Spotify ids, so nothing talks to Spotify) with albums and tracks;
  - shows in hub cities, each with a fan allocation in a different state: open, upcoming, open with a high
    raw-score threshold, closed with the selection draw run (selected and not-selected fans, +1 guests), draft,
    cancelled; registrations through register_for_allocation(), so every snapshot and rule is the real one. Some
    fans bring a +1, a few have withdrawn.
  - the staff account dev_staff (tools/dev/allocations.py uses it too).

Sign in from the app with dev sign-in (Auth__DevSignIn=true in .env) as any handle the script prints, e.g. dev_staff.
Nobody has a Spotify connection (a fake one would make the hourly sync fail), so "connect Spotify" still works.
The artists' catalogs count as loaded now; after a week the worker's weekly catalog refresh will try Spotify for the
'seed:' ids and log a failed job per artist. Re-seed (--wipe) or ignore those.
Refuses to run on a database that already holds seed data: wipe first. Stdlib only.

LOCAL ONLY. The data is fake and the artists are not on Spotify. Never run this against beta or prod.
"""

import argparse
import datetime
import json
import math
import os
import random
import subprocess
import sys
import uuid

sys.path.insert(0, os.path.dirname(__file__))
from spotify_connect import COMPOSE_FILE  # noqa: E402 (shared dev helpers)

# Hub cities from migration 0010 (the show cities and most fans' hubs come from these).
CITIES = {
    "us-ny-new-york": "New York", "us-ca-los-angeles": "Los Angeles", "us-il-chicago": "Chicago",
    "us-tx-dallas": "Dallas", "us-tx-houston": "Houston", "us-dc-washington": "Washington",
    "us-pa-philadelphia": "Philadelphia", "us-ga-atlanta": "Atlanta", "us-fl-miami": "Miami",
    "us-ma-boston": "Boston", "us-ca-san-francisco": "San Francisco", "us-wa-seattle": "Seattle",
    "us-mn-minneapolis": "Minneapolis", "us-co-denver": "Denver", "us-tn-nashville": "Nashville",
    "us-or-portland": "Portland", "us-tx-austin": "Austin", "us-mi-detroit": "Detroit",
}
FIRST_NAMES = ["Ava", "Noah", "Mia", "Liam", "Zoe", "Ethan", "Isla", "Mateo", "Nora", "Kai", "Lena", "Owen", "Ruby",
               "Theo", "Ivy", "Jonah", "Maya", "Felix", "Sage", "Elias", "Luna", "Milo", "Hazel", "Arjun", "Priya",
               "Diego", "Sofia", "Amara", "Jude", "Nia", "Rowan", "Tess", "Omar", "Yuki", "Leo"]
LAST_NAMES = ["Reyes", "Park", "Okafor", "Lindqvist", "Nakamura", "Costa", "Brennan", "Haddad", "Kowalski", "Mbeki",
              "Sato", "Alvarez", "Novak", "Delgado", "Osei", "Fischer", "Quinn", "Ramos", "Singh", "Moreau", "Walsh",
              "Ibarra", "Chen", "Dubois", "Petrov", "Nguyen", "Hale", "Varga", "Adeyemi", "Larsen"]
ARTISTS = ["Glass Harbor", "Neon Orchard", "The Late Trains", "Marrow & Pine", "Saltwater Choir", "Ivy Signal",
           "Paper Lanterns", "Cobalt Youth", "Hollow Pines", "Velvet Static", "Northern Attic", "Candle District"]
ALBUM_WORDS = (["Quiet", "Electric", "Paper", "Midnight", "Golden", "Broken", "Summer", "Static", "Hollow", "Violet"],
               ["Rooms", "Gardens", "Signals", "Weather", "Letters", "Harbors", "Machines", "Lights", "Tides", "Maps"])
TRACK_WORDS = (["Last", "First", "Open", "Slow", "Wild", "Lost", "Blue", "Second", "Long", "Low"],
               ["Window", "Highway", "Season", "Sister", "Radio", "River", "Ceiling", "Weekend", "Engine", "Shore"])
VENUES = ["The Riviera", "Union Hall", "Paramount Theatre", "The Fillmore", "Brooklyn Steel", "The Anthem",
          "First Avenue", "The Wiltern", "Stubb's", "9:30 Club", "Royale", "The Showbox", "Ryman Auditorium",
          "Crystal Ballroom", "Saint Andrew's Hall", "Ogden Theatre", "Terminal West", "The Fonda"]
REVIEWS = [
    "Took three listens to click, and then it wouldn't leave. The sequencing is the whole point here: side B answers "
    "every question side A asks, and the closer lands like a door shutting quietly.",
    "Louder and looser than anything they've done, with drums that sound like they were recorded in a stairwell. "
    "Not every song earns its length, but the ones that do are the best they've written.",
    "A late-night record. The vocals sit so far back in the mix that you lean in, and the lyrics reward it: small "
    "domestic scenes that turn out to be about leaving. Still thinking about the title track.",
    "I wanted to love this more than I do. The singles are great and the production is gorgeous, but the middle "
    "stretch blurs together. Worth it for the last two songs alone, though.",
    "Finally, the live energy on a studio record. You can hear the room, the count-ins, somebody laughing at the end "
    "of track six. It feels like being let in on something.",
]
ALLOCATION_STATES = ["open", "upcoming", "open", "open_high_threshold", "selected", "draft", "cancelled", "open",
                     "upcoming", "open"]


def lit(value):
    """SQL literal for str, int, float, bool, None, datetime, date, or a list of str."""
    if value is None:
        return "NULL"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return repr(value)
    if isinstance(value, uuid.UUID):
        return f"'{value}'"
    if isinstance(value, datetime.datetime):
        return f"'{value.isoformat(timespec='seconds')}'"
    if isinstance(value, datetime.date):
        return f"'{value.isoformat()}'"
    if isinstance(value, list):
        return "ARRAY[" + ", ".join(lit(v) for v in value) + "]::text[]"
    return "'" + str(value).replace("'", "''") + "'"


def insert(table, columns, rows, chunk=200):
    """Multi-row INSERT statements."""
    out = []
    for start in range(0, len(rows), chunk):
        values = ",\n  ".join("(" + ", ".join(lit(v) for v in row) + ")" for row in rows[start:start + chunk])
        out.append(f"INSERT INTO {table} ({', '.join(columns)}) VALUES\n  {values};")
    return "\n".join(out)


def slug(name):
    return "".join(ch if ch.isalnum() else "-" for ch in name.lower()).strip("-").replace("--", "-")


def lognormal_curve(median, sigma):
    """Same shape as workers/artist-onboarding/ProvisionalBaseline.cs: 61 [score, cdf] points over -3σ..+3σ."""
    mu = math.log(median)
    return [[round(math.exp(mu + sigma * z / 10), 3), round(0.5 * (1 + math.erf(z / 10 / math.sqrt(2))), 5)]
            for z in range(-30, 31)]


def generate(fans, artists, shows, seed):
    rng = random.Random(seed)
    now = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0)
    new_id = lambda: uuid.UUID(int=rng.getrandbits(128), version=4)  # noqa: E731 (deterministic ids)
    city_ids = list(CITIES)
    sql = ["\\set ON_ERROR_STOP on", "BEGIN;",
           "DO $$ BEGIN IF EXISTS (SELECT 1 FROM artists WHERE spotify_artist_id LIKE 'seed:%') THEN\n"
           "  RAISE EXCEPTION 'seed data is already present; wipe the local database first "
           "(testScripts/wipeLocalDb.sh or seed.py --wipe)'; END IF; END $$;"]

    # --- staff and fans -------------------------------------------------------------------------------------------
    staff_id = new_id()
    users = [(staff_id, "dev_staff", "dev_staff@example.test", "Dev Staff", True, now - datetime.timedelta(days=400))]
    handles = {"dev_staff"}
    fan_ids = []
    for _ in range(fans):
        first, last = rng.choice(FIRST_NAMES), rng.choice(LAST_NAMES)
        handle = f"{first}_{last}".lower()
        while handle in handles:
            handle = f"{first}_{last}{rng.randint(2, 999)}".lower()
        handles.add(handle)
        user_id = new_id()
        fan_ids.append(user_id)
        users.append((user_id, handle, f"{handle}@example.test", f"{first} {last}", False,
                      now - datetime.timedelta(days=rng.uniform(1, 365))))
    sql.append("-- Accounts. Dev sign-in (Auth__DevSignIn) signs in as Google subject dev:<handle>.")
    sql.append(insert("users", ["id", "handle", "email", "display_name", "is_staff", "created_at"], users))
    sql.append(insert("auth_identities", ["provider", "subject", "user_id"],
                      [("google", f"dev:{u[1]}", u[0]) for u in users]))
    sql.append("-- Hub cities (1 to 3 each, through set_hub_cities so the same rules apply).")
    hubs = {}
    for user_id in fan_ids:
        hubs[user_id] = rng.sample(city_ids, rng.choice([1, 2, 2, 3]))
    hub_rows = ",\n  ".join(f"({lit(u)}::uuid, {lit(c)})" for u, c in hubs.items())
    sql.append(f"SELECT count(*) AS fans_with_hubs FROM (VALUES\n  {hub_rows}) AS v (u, c) CROSS JOIN LATERAL set_hub_cities(u, c) AS next_change;")

    # --- artists, albums, tracks ----------------------------------------------------------------------------------
    sql.append("-- Artists start onboarding (the activation trigger needs the baseline first), then go active.")
    artist_rows, baseline_rows, album_rows, track_rows = [], [], [], []
    catalog = {}  # artist_id -> {"albums": [(album_id, [track_ids])]}
    for name in ARTISTS[:artists]:
        artist_id = new_id()
        albums = []
        for n in range(rng.randint(3, 6)):
            album_id = new_id()
            title = f"{rng.choice(ALBUM_WORDS[0])} {rng.choice(ALBUM_WORDS[1])}"
            release = datetime.date(rng.randint(2012, 2026), rng.randint(1, 12), rng.randint(1, 28))
            if release > now.date():
                release = now.date() - datetime.timedelta(days=rng.randint(10, 300))
            album_rows.append((album_id, artist_id, title, release, f"seed:{slug(name)}:{n}", "album", now))
            tracks = []
            for t in range(1, rng.randint(8, 12) + 1):
                track_id = new_id()
                tracks.append(track_id)
                track_rows.append((track_id, album_id, f"{rng.choice(TRACK_WORDS[0])} {rng.choice(TRACK_WORDS[1])}",
                                   t, rng.randint(5, 80), f"seed:{slug(name)}:{n}:{t}"))
            albums.append((album_id, tracks))
        catalog[artist_id] = {"name": name, "albums": albums}
        artist_rows.append((artist_id, name, f"seed:{slug(name)}", "onboarding", now,
                            f"https://{slug(name)}.example.test", f"https://{slug(name)}.example.test/shop"))
        baseline_rows.append((artist_id, "lognormal",
                              json.dumps({"mu": math.log(15), "sigma": 1, "provisional": True, "source": "seed"}),
                              json.dumps({"source": "seed", "albums": len(albums),
                                          "tracks": sum(len(t) for _, t in albums)}),
                              json.dumps(lognormal_curve(15, 1)), 200))
    sql.append(insert("artists", ["id", "name", "spotify_artist_id", "status", "catalog_loaded_at", "website_url", "merch_url"],
                      artist_rows))
    sql.append(insert("artist_baselines", ["artist_id", "dist_family", "dist_params", "proxy_metrics", "curve", "blend_k"],
                      baseline_rows))
    sql.append("UPDATE artists SET status = 'active' WHERE spotify_artist_id LIKE 'seed:%';")
    sql.append(insert("albums", ["id", "artist_id", "title", "release_date", "spotify_album_id", "album_type", "refreshed_at"],
                      album_rows))
    sql.append(insert("tracks", ["id", "album_id", "title", "track_number", "popularity", "spotify_track_id"], track_rows))

    # --- fandom: signals, liked tracks, journal, proof -------------------------------------------------------------
    sql.append("-- Fandom. Journal logs and verified submissions award points through the real triggers.")
    artist_ids = list(catalog)
    signal_rows, like_rows, log_rows, submission_rows = [], [], [], []
    follows = {}  # artist_id -> [user_id]
    for user_id in fan_ids:
        for artist_id in rng.sample(artist_ids, min(len(artist_ids), rng.choice([1, 1, 2, 2, 3, 4]))):
            follows.setdefault(artist_id, []).append(user_id)
            level = rng.random()  # 0: barely listens, 1: superfan
            all_tracks = [t for _, tracks in catalog[artist_id]["albums"] for t in tracks]
            liked = rng.sample(all_tracks, min(len(all_tracks), int(level ** 2 * 60)))
            for track_id in liked:
                like_rows.append((user_id, track_id, "liked", now - datetime.timedelta(days=rng.uniform(1, 900))))
            distinct_albums = len({a for a, tracks in catalog[artist_id]["albums"] if set(tracks) & set(liked)})
            signal_rows.append((user_id, artist_id, len(liked), int(level ** 2 * 30), distinct_albums,
                                rng.randint(1, 50) if level > 0.7 else None, int(level ** 3 * 50),
                                now - datetime.timedelta(hours=rng.uniform(1, 72))))
            logs = int(level * 4)
            for album_id, _ in rng.sample(catalog[artist_id]["albums"], min(logs, len(catalog[artist_id]["albums"]))):
                listened = now.date() - datetime.timedelta(days=rng.randint(0, 400))
                review = rng.choice(REVIEWS) if rng.random() < 0.3 else None
                log_rows.append((new_id(), user_id, album_id, rng.randint(4, 10), review, listened, False,
                                 datetime.datetime.combine(listened, datetime.time(20, 0), datetime.timezone.utc)))
            if level > 0.85:
                for kind in rng.sample(["ticket", "merch", "ticket"], rng.randint(1, 2)):
                    submission_rows.append((new_id(), user_id, artist_id, kind,
                                            now - datetime.timedelta(days=rng.randint(1, 200))))
    sql.append(insert("user_artist_signals", ["user_id", "artist_id", "liked_tracks", "playlist_tracks", "distinct_albums",
                                              "top_artist_rank", "recent_plays", "synced_at"], signal_rows))
    sql.append(insert("user_track_likes", ["user_id", "track_id", "source", "saved_at"], like_rows))
    sql.append(insert("album_logs", ["id", "user_id", "album_id", "rating", "review", "listened_on", "is_relisten", "created_at"],
                      log_rows))
    if submission_rows:
        sql.append(insert("submissions", ["id", "user_id", "artist_id", "kind", "submitted_at"],
                          [(s[0], s[1], s[2], s[3], s[4]) for s in submission_rows]))
        ids = ", ".join(lit(s[0]) for s in submission_rows)
        sql.append(f"UPDATE submissions SET status = 'verified' WHERE id IN ({ids});")

    # --- scores -----------------------------------------------------------------------------------------------------
    sql.append("""-- Provisional scores so registrations work right away: raw score from v_raw_scores, mid-rank percentile
-- among the artist's seeded fans. request_full_rescore() queues the real scoring (blended with the baseline) for a
-- running worker, which overwrites these.
INSERT INTO user_artist_scores (user_id, artist_id, raw_score, percentile, is_estimated, baseline_weight, breakdown)
SELECT user_id, artist_id, raw_score,
       round(100.0 * (rank() OVER (PARTITION BY artist_id ORDER BY raw_score) - 1
                      + 0.5 * count(*) OVER (PARTITION BY artist_id, raw_score))
             / count(*) OVER (PARTITION BY artist_id), 2),
       false, 0, '{"source": "seed"}'
  FROM v_raw_scores
 WHERE artist_id IN (SELECT id FROM artists WHERE spotify_artist_id LIKE 'seed:%')
ON CONFLICT (user_id, artist_id) DO UPDATE
  SET raw_score = EXCLUDED.raw_score, percentile = EXCLUDED.percentile, is_estimated = EXCLUDED.is_estimated,
      baseline_weight = EXCLUDED.baseline_weight, breakdown = EXCLUDED.breakdown, computed_at = now();
SELECT request_full_rescore() AS rescore_jobs_queued;""")

    # --- shows, allocations, registrations ----------------------------------------------------------------------
    sql.append("-- Shows and allocations, one state each; registrations through register_for_allocation().")
    event_rows, allocation_rows, candidates, cancelled, withdrawals, plus_ones, selections = [], [], [], [], [], [], []
    day = datetime.timedelta(days=1)
    for i in range(shows):
        artist_id = artist_ids[i % len(artist_ids)]
        city_id = rng.choice(city_ids)
        venue = rng.choice(VENUES)
        starts = now + datetime.timedelta(days=rng.randint(12, 75), hours=rng.choice([19, 20, 21]) - now.hour)
        event_id = new_id()
        event_rows.append((event_id, artist_id, f"{catalog[artist_id]['name']} at {venue}", venue, CITIES[city_id], "US",
                           city_id, starts, staff_id))
        state = ALLOCATION_STATES[i % len(ALLOCATION_STATES)]
        # threshold None: the database applies the default (a fan with two saved albums) when publishing.
        opens, closes, published, threshold = now - day, min(now + 4 * day, starts - 2 * day), True, None
        if state == "upcoming":
            opens, closes = now + 2 * day, now + 6 * day
        elif state == "open_high_threshold":
            threshold = 60
        elif state == "selected":
            opens, closes = now - 3 * day, "clock_timestamp() + interval '2 seconds'"
            selections.append(allocation_id := new_id())
        elif state == "draft":
            opens, closes, published = now + 3 * day, now + 7 * day, False
        allocation_id = selections[-1] if state == "selected" else new_id()
        allocation_rows.append((allocation_id, event_id, "Fan allocation", rng.choice([20, 40, 60, 100, 150]),
                                rng.choice([3500, 4500, 6000, 8500, 12000]), threshold, opens, closes,
                                "now()" if published else None, staff_id, state))
        if state == "cancelled":
            cancelled.append(allocation_id)
        if state in ("open", "open_high_threshold", "selected", "cancelled"):
            fans_of_artist = follows.get(artist_id, [])
            for user_id in rng.sample(fans_of_artist, int(len(fans_of_artist) * 0.7)):
                candidates.append((user_id, allocation_id))
                if state == "open" and rng.random() < 0.05:
                    withdrawals.append((user_id, allocation_id))
                elif rng.random() < 0.2:
                    plus_ones.append((user_id, allocation_id))
        if i == 0:
            allocation_rows.append((new_id(), event_id, "Balcony", 10, 15000, 80, now + 3 * day, now + 7 * day, None,
                                    staff_id, "draft"))
    sql.append(insert("events", ["id", "artist_id", "name", "venue", "city", "country", "city_id", "starts_at", "created_by"],
                      event_rows))
    values = ",\n  ".join(
        "(" + ", ".join([lit(a[0]), lit(a[1]), lit(a[2]), lit(a[3]), lit(a[4]), lit(a[5]), lit(a[6]),
                         a[7] if isinstance(a[7], str) else lit(a[7]), a[8] if isinstance(a[8], str) else "NULL",
                         lit(a[9])]) + f") /* {a[10]} */"
        for a in allocation_rows)
    sql.append("INSERT INTO allocations (id, event_id, name, tickets, face_value_cents, min_raw_score, "
               f"registration_opens_at, registration_closes_at, published_at, created_by) VALUES\n  {values};")
    if candidates:
        rows = ",\n  ".join(f"({lit(u)}::uuid, {lit(a)}::uuid)" for u, a in candidates)
        sql.append("SELECT count(register_for_allocation(u, a)) AS registered FROM (VALUES\n  "
                   f"{rows}) AS c (u, a) WHERE registration_blocker(u, a) IS NULL;")
    if withdrawals:
        rows = ",\n  ".join(f"({lit(u)}::uuid, {lit(a)}::uuid)" for u, a in withdrawals)
        sql.append(f"SELECT count(*) FILTER (WHERE withdraw_registration(u, a)) AS withdrawn FROM (VALUES\n  {rows}) AS c (u, a);")
    if plus_ones:
        rows = ",\n  ".join(f"({lit(u)}::uuid, {lit(a)}::uuid)" for u, a in plus_ones)
        sql.append("-- A +1 while registration is open (what PUT /allocations/{id}/registration with plus_one does).\n"
                   "UPDATE allocation_registrations r SET plus_one = true FROM (VALUES\n  "
                   f"{rows}) AS c (u, a) WHERE r.user_id = c.u AND r.allocation_id = c.a AND r.withdrawn_at IS NULL;")
    for allocation_id in cancelled:
        sql.append(f"UPDATE allocations SET cancelled_at = now() WHERE id = {lit(allocation_id)};")
    sql.append("DO $$ BEGIN PERFORM pg_sleep(2.5); END $$;  -- the 'selected' allocation's registration closes")
    for allocation_id in selections:
        sql.append(f"SELECT selected_fans, seats_assigned, registrations FROM run_allocation_selection({lit(allocation_id)}, {lit(staff_id)});")
    sql.append("COMMIT;")
    sql.append("""SELECT 'fans' AS seeded, count(*) FROM users WHERE NOT is_staff AND deleted_at IS NULL
UNION ALL SELECT 'artists (active)', count(*) FROM artists WHERE status = 'active'
UNION ALL SELECT 'albums', count(*) FROM albums
UNION ALL SELECT 'album logs', count(*) FROM album_logs
UNION ALL SELECT 'ledger points', coalesce(sum(points), 0) FROM score_events WHERE reversed_at IS NULL
UNION ALL SELECT 'scores', count(*) FROM user_artist_scores
UNION ALL SELECT 'shows', count(*) FROM events
UNION ALL SELECT 'allocations', count(*) FROM allocations
UNION ALL SELECT 'registrations (live)', count(*) FROM allocation_registrations WHERE withdrawn_at IS NULL
UNION ALL SELECT 'registrations (withdrawn)', count(*) FROM allocation_registrations WHERE withdrawn_at IS NOT NULL
UNION ALL SELECT 'registrations with a +1', count(*) FROM allocation_registrations WHERE plus_one AND withdrawn_at IS NULL
UNION ALL SELECT 'fans selected by the draw', count(*) FROM allocation_registrations WHERE outcome = 'selected';""")
    examples = [u[1] for u in users[1:6]]
    return "\n\n".join(sql) + "\n", examples


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fans", type=int, default=200)
    parser.add_argument("--artists", type=int, default=8, help=f"up to {len(ARTISTS)}")
    parser.add_argument("--shows", type=int, default=10)
    parser.add_argument("--seed", type=int, default=1, help="random seed; the same seed gives the same data")
    parser.add_argument("--wipe", action="store_true", help="run testScripts/wipeLocalDb.sh first (it asks)")
    parser.add_argument("--sql", action="store_true", help="print the SQL instead of running it")
    args = parser.parse_args()
    if not 1 <= args.artists <= len(ARTISTS):
        sys.exit(f"--artists must be between 1 and {len(ARTISTS)}.")
    if args.fans < 1 or args.shows < 1:
        sys.exit("--fans and --shows must be at least 1.")

    sql, examples = generate(args.fans, args.artists, args.shows, args.seed)
    if args.sql:
        sys.stdout.write(sql)
        return

    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
    if args.wipe:
        subprocess.run([os.path.join(root, "testScripts", "wipeLocalDb.sh")], check=True)
    # The compose file needs POSTGRES_PASSWORD; take it from .env when the shell hasn't exported it.
    env_file = os.path.join(root, ".env")
    compose = ["docker", "compose"] + (["--env-file", env_file] if os.path.exists(env_file) else []) + ["-f", COMPOSE_FILE]
    command = compose + ["exec", "-T", "postgres",
                         "psql", "-U", "fandom", "-d", os.environ.get("DEV_DATABASE", "fandom_score"), "-q"]
    result = subprocess.run(command, input=sql, text=True)
    if result.returncode != 0:
        sys.exit("Seeding failed; nothing was written (it is one transaction). "
                 "Is the compose database running with migrations applied, and the database empty?")
    print(f"\nSeeded. Sign in from the app with dev sign-in as dev_staff (staff) or a fan: {', '.join(examples)}, ...")
    print("Start the worker (dotnet run --project workers) to replace the provisional scores with the real ones.")


if __name__ == "__main__":
    main()
