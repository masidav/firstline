#!/usr/bin/env python3
"""Fill the LOCAL development database (dev, 127.0.0.1) with fake fans, artists, shows, allocations, and registrations.

Usage, from the repo root with the compose database running and migrations applied (docs/DEVELOPMENT.md):
  python3 tools/dev/seed.py                          # 200 fans, 8 artists, 10 shows
  python3 tools/dev/seed.py --fans 1000 --seed 7     # more fans, a different deterministic data set
  python3 tools/dev/seed.py --wipe                   # wipe the local database first (testScripts/wipeLocalDb.sh asks)
  python3 tools/dev/seed.py --sql | psql ...         # print the SQL instead of running it (CI does this)

What you get, all deterministic for a given --seed:
  - fans with hub cities, Spotify-like signals, album journal logs (which award ledger points through the real
    triggers; some followers-only), a few verified ticket and merch submissions (merch shared to the feed, with a
    generated placeholder photo written to the API's local storage so the feed shows an image), and a fandom score
    per followed artist;
  - follows between fans (2 to 6 each), grab bags (up to five favourite songs by the artists they listen to), and
    answers to today's music prompt from about a third of fans, which fill the home feed;
  - active artists with albums and tracks. With Spotify credentials (Spotify__ClientId and Spotify__ClientSecret,
    from the environment or the repo's .env) these are REAL artists fetched from Spotify with the app token: real
    names, Spotify ids, photos, album covers, and track lists, so covers, Spotify links, song search, and the
    bouncer door look like the real thing (REAL_ARTISTS below; --fake to skip). Without credentials (CI) they are
    made up, with 'seed:' ids;
  - shows in hub cities, each with a fan allocation in a different state: open, upcoming, open with a high
    raw-score threshold, closed with the selection draw run (selected and not-selected fans, +1 guests), draft,
    cancelled; registrations through register_for_allocation(), so every snapshot and rule is the real one. Some
    fans bring a +1, a few have withdrawn.
  - the staff account dev_staff (tools/dev/allocations.py uses it too).

Sign in from the app with dev sign-in (Auth__DevSignIn=true in .env) as any handle the script prints, e.g. dev_staff.
Nobody has a Spotify connection (a fake one would make the hourly sync fail), so "connect Spotify" still works.
Photo files go to --storage (default services/api/.local-storage, the API's Storage__LocalPath); with --sql they are
not written, so the file links 404.
The artists' catalogs count as loaded now. With real artists the worker's weekly catalog refresh keeps them current;
with made-up ones it will log a failed job per 'seed:' id after a week. Re-seed (--wipe) or ignore those.
Refuses to run on a database that already holds seed data, or that already tracks one of the real artists: wipe
first. Stdlib only.

LOCAL ONLY. The fans and their activity are fake. Never run this against beta or prod.
"""

import argparse
import base64
import datetime
import hashlib
import json
import math
import os
import random
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zlib

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
# Real artists for the dev seed, looked up on Spotify by name (exact name match preferred). Varied on purpose.
REAL_ARTISTS = ["Radiohead", "Geese", "Phoebe Bridgers", "Fontaines D.C.", "Big Thief", "Mitski", "Japanese Breakfast",
                "Wet Leg", "Turnstile", "Alvvays", "Black Country, New Road", "boygenius"]
REAL_ALBUMS_PER_ARTIST = 4
SPOTIFY_TOKEN_URL = "https://accounts.spotify.com/api/token"
SPOTIFY_API = "https://api.spotify.com/v1/"
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


def placeholder_png(rng, width=640, height=480):
    """A small valid PNG (stdlib only): a dark background with a lighter striped block, in colours from rng. Stands
    in for the fan's merch photo; the real upload flow stores JPEG/PNG under the same storage keys."""
    back = tuple(rng.randint(20, 70) for _ in range(3))
    block = tuple(min(255, c + rng.randint(90, 160)) for c in back)
    stripe = tuple(rng.randint(150, 255) for _ in range(3))
    left, top, right, bottom = width // 5, height // 4, width * 4 // 5, height * 3 // 4
    rows = bytearray()
    for y in range(height):
        rows.append(0)  # PNG filter type: none
        for x in range(width):
            if top <= y < bottom and left <= x < right:
                rows.extend(stripe if (y - top) // 24 % 3 == 1 else block)
            else:
                rows.extend(back)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(rows), 6)) + chunk(b"IEND", b""))


def fake_catalog(count, rng, now):
    """Made-up artists (no Spotify): the same shape fetch_real_catalog returns."""
    catalog = []
    for name in ARTISTS[:count]:
        albums = []
        for n in range(rng.randint(3, 6)):
            release = datetime.date(rng.randint(2012, 2026), rng.randint(1, 12), rng.randint(1, 28))
            if release > now.date():
                release = now.date() - datetime.timedelta(days=rng.randint(10, 300))
            tracks = [{"title": f"{rng.choice(TRACK_WORDS[0])} {rng.choice(TRACK_WORDS[1])}", "track_number": t,
                       "spotify_id": f"seed:{slug(name)}:{n}:{t}"} for t in range(1, rng.randint(8, 12) + 1)]
            albums.append({"title": f"{rng.choice(ALBUM_WORDS[0])} {rng.choice(ALBUM_WORDS[1])}", "spotify_id": f"seed:{slug(name)}:{n}",
                           "release_date": release, "precision": "day", "cover_url": None, "tracks": tracks})
        catalog.append({"name": name, "spotify_id": f"seed:{slug(name)}", "image_url": None, "real": False, "albums": albums,
                        "website_url": f"https://{slug(name)}.example.test", "merch_url": f"https://{slug(name)}.example.test/shop"})
    return catalog


def spotify_credentials(root):
    """(client id, client secret) from the environment, else from the repo's .env; None when either is missing."""
    values = {key: os.environ.get(key, "") for key in ("Spotify__ClientId", "Spotify__ClientSecret")}
    env_file = os.path.join(root, ".env")
    if not all(values.values()) and os.path.exists(env_file):
        with open(env_file, encoding="utf-8") as handle:
            for line in handle:
                key, sep, value = line.strip().partition("=")
                if sep and key in values and not values[key]:
                    values[key] = value.strip().strip("'\"")
    return (values["Spotify__ClientId"], values["Spotify__ClientSecret"]) if all(values.values()) else None


def _read_json(request, opener):
    """GET or POST with up to three tries on 429 and 5xx (Retry-After honoured, capped at 10 s)."""
    for attempt in range(1, 4):
        try:
            with opener(request, timeout=20) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if attempt == 3 or (e.code != 429 and e.code < 500):
                raise
            time.sleep(min(10, int(e.headers.get("Retry-After") or attempt)))
    raise RuntimeError("unreachable")


def parse_release(raw, precision):
    """Spotify's release date string ('2023', '2023-06', '2023-06-23') as a date and its precision."""
    parts = (raw or "1970").split("-")
    precision = precision or ("day" if len(parts) == 3 else "month" if len(parts) == 2 else "year")
    return datetime.date(int(parts[0]), int(parts[1]) if len(parts) > 1 else 1, int(parts[2]) if len(parts) > 2 else 1), precision


def fetch_real_catalog(names, credentials, albums_per_artist=REAL_ALBUMS_PER_ARTIST, opener=urllib.request.urlopen):
    """Real artists from Spotify with the app token (client credentials): photo, newest albums, and their tracks.
    Only endpoints and limits available to development-mode apps (search 10, artist albums 10 per page)."""
    client_id, secret = credentials
    basic = base64.b64encode(f"{client_id}:{secret}".encode()).decode()
    token = _read_json(urllib.request.Request(
        SPOTIFY_TOKEN_URL, data=b"grant_type=client_credentials", method="POST",
        headers={"Authorization": f"Basic {basic}", "Content-Type": "application/x-www-form-urlencoded"}), opener)["access_token"]

    def get(path):
        return _read_json(urllib.request.Request(SPOTIFY_API + path, headers={"Authorization": f"Bearer {token}"}), opener)

    def pick_image(images, minimum=0):
        images = [i for i in images or [] if i.get("url")]
        fitting = sorted((i for i in images if (i.get("width") or 0) >= minimum), key=lambda i: i.get("width") or 0)
        return (fitting[0] if fitting and minimum else max(images, key=lambda i: i.get("width") or 0, default=None) or {}).get("url")

    catalog = []
    for name in names:
        found = get("search?" + urllib.parse.urlencode({"type": "artist", "limit": 5, "q": name}))["artists"]["items"]
        if not found:
            print(f"Spotify has no artist called {name}; skipped.", file=sys.stderr)
            continue
        artist = next((a for a in found if a["name"].lower() == name.lower()), found[0])
        releases = get(f"artists/{artist['id']}/albums?include_groups=album&market=US&limit=10")["items"]
        albums, titles, track_ids = [], set(), set()
        for release in releases:
            if len(albums) >= albums_per_artist or release["name"].lower() in titles:
                continue   # one edition per title (deluxe reissues share names)
            titles.add(release["name"].lower())
            tracks = get(f"albums/{release['id']}/tracks?market=US&limit=50")["items"]
            date, precision = parse_release(release.get("release_date"), release.get("release_date_precision"))
            albums.append({"title": release["name"], "spotify_id": release["id"], "release_date": date, "precision": precision,
                           "cover_url": pick_image(release.get("images")),
                           "tracks": [{"title": t["name"], "track_number": t.get("track_number") or n, "spotify_id": t["id"]}
                                      for n, t in enumerate(tracks, 1)
                                      if t.get("id") and not (t["id"] in track_ids or track_ids.add(t["id"]))]})
        if albums:
            catalog.append({"name": artist["name"], "spotify_id": artist["id"], "image_url": pick_image(artist.get("images"), 300),
                            "real": True, "albums": albums, "website_url": None, "merch_url": None})
    return catalog


def lognormal_curve(median, sigma):
    """Same shape as workers/artist-onboarding/ProvisionalBaseline.cs: 61 [score, cdf] points over -3σ..+3σ."""
    mu = math.log(median)
    return [[round(math.exp(mu + sigma * z / 10), 3), round(0.5 * (1 + math.erf(z / 10 / math.sqrt(2))), 5)]
            for z in range(-30, 31)]


def generate(fans, artists, shows, seed, catalog_source=None):
    """The seed SQL. catalog_source: artists from fetch_real_catalog (real), or None for made-up ones."""
    rng = random.Random(seed)
    now = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0)
    new_id = lambda: uuid.UUID(int=rng.getrandbits(128), version=4)  # noqa: E731 (deterministic ids)
    city_ids = list(CITIES)
    files = []  # (storage key, bytes): placeholder photos to write under the API's local storage
    source = (catalog_source or fake_catalog(artists, rng, now))[:artists]
    seeded_ids = [a["spotify_id"] for a in source]
    sql = ["\\set ON_ERROR_STOP on", "BEGIN;",
           "DO $$ BEGIN IF EXISTS (SELECT 1 FROM users WHERE handle = 'dev_staff')\n"
           f"  OR EXISTS (SELECT 1 FROM artists WHERE spotify_artist_id LIKE 'seed:%' OR spotify_artist_id = ANY({lit(seeded_ids)})) THEN\n"
           "  RAISE EXCEPTION 'seed data (or one of its artists) is already present; wipe the local database first "
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
    catalog = {}  # artist_id -> {"name", "albums": [(album_id, [track_ids])]}
    for entry in source:
        name = entry["name"]
        artist_id = new_id()
        albums = []
        for album in entry["albums"]:
            album_id = new_id()
            album_rows.append((album_id, artist_id, album["title"], album["release_date"], album["precision"], album["spotify_id"],
                               "album", album["cover_url"], now))
            tracks = []
            for track in album["tracks"]:
                track_id = new_id()
                tracks.append(track_id)
                track_rows.append((track_id, album_id, track["title"], track["track_number"], rng.randint(5, 80), track["spotify_id"]))
            albums.append((album_id, tracks))
        catalog[artist_id] = {"name": name, "albums": albums}
        artist_rows.append((artist_id, name, entry["spotify_id"], "onboarding", now, entry["website_url"], entry["merch_url"],
                            entry["image_url"]))
        baseline_rows.append((artist_id, "lognormal",
                              json.dumps({"mu": math.log(15), "sigma": 1, "provisional": True, "source": "seed"}),
                              json.dumps({"source": "seed", "albums": len(albums),
                                          "tracks": sum(len(t) for _, t in albums)}),
                              json.dumps(lognormal_curve(15, 1)), 200))
    sql.append(insert("artists", ["id", "name", "spotify_artist_id", "status", "catalog_loaded_at", "website_url", "merch_url",
                                  "image_url"], artist_rows))
    sql.append(insert("artist_baselines", ["artist_id", "dist_family", "dist_params", "proxy_metrics", "curve", "blend_k"],
                      baseline_rows))
    sql.append(f"UPDATE artists SET status = 'active' WHERE spotify_artist_id = ANY({lit(seeded_ids)});")
    sql.append(insert("albums", ["id", "artist_id", "title", "release_date", "release_date_precision", "spotify_album_id", "album_type",
                                 "cover_url", "refreshed_at"], album_rows))
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
                visibility = "followers" if rng.random() < 0.15 else "public"
                log_rows.append((new_id(), user_id, album_id, rng.randint(4, 10), review, listened, False,
                                 datetime.datetime.combine(listened, datetime.time(20, 0), datetime.timezone.utc), visibility))
            if level > 0.85:
                for kind in rng.sample(["ticket", "merch", "ticket"], rng.randint(1, 2)):
                    submission_rows.append((new_id(), user_id, artist_id, kind,
                                            now - datetime.timedelta(days=rng.randint(1, 200))))
    sql.append(insert("user_artist_signals", ["user_id", "artist_id", "liked_tracks", "playlist_tracks", "distinct_albums",
                                              "top_artist_rank", "recent_plays", "synced_at"], signal_rows))
    sql.append(insert("user_track_likes", ["user_id", "track_id", "source", "saved_at"], like_rows))
    sql.append(insert("album_logs", ["id", "user_id", "album_id", "rating", "review", "listened_on", "is_relisten", "created_at",
                                     "visibility"], log_rows))
    if submission_rows:
        sql.append(insert("submissions", ["id", "user_id", "artist_id", "kind", "submitted_at"],
                          [(s[0], s[1], s[2], s[3], s[4]) for s in submission_rows]))
        ids = ", ".join(lit(s[0]) for s in submission_rows)
        sql.append(f"UPDATE submissions SET status = 'verified' WHERE id IN ({ids});")
        sql.append("-- Verified merch goes on the feed, as PUT /submissions/{id}/share does, with a placeholder photo file.\n"
                   f"UPDATE submissions SET shared_at = submitted_at + interval '2 days' WHERE kind = 'merch' AND id IN ({ids});")
        file_values = []
        for submission in submission_rows:
            if submission[3] != "merch":
                continue
            file_id = new_id()
            png = placeholder_png(rng)
            # Already redacted and archived (migration 0029): the placeholders have no codes, and the feed only shows
            # shared photos whose files are archived.
            key = f"archive/seed-{file_id}.png"
            files.append((key, png))
            file_values.append(f"({lit(file_id)}, {lit(submission[0])}, {lit(key)}, decode({lit(hashlib.sha256(png).hexdigest())}, 'hex'), "
                               f"'image/png', {len(png)}, {lit(submission[4])}, 'archive', true, now(), 'archived', 0)")
        if file_values:
            sql.append("INSERT INTO submission_files (id, submission_id, storage_key, sha256, mime_type, bytes, uploaded_at,\n"
                       "                              tier, redacted, archived_at, redaction_state, codes_redacted) VALUES\n  "
                       + ",\n  ".join(file_values) + ";")

    # --- follows and grab bags (0018) ----------------------------------------------------------------------------
    sql.append("-- Follows (no approval) and grab bags: five favourite songs by artists the fan listens to, with made-up\n"
               "-- 22-character Spotify ids (the seed artists aren't on Spotify). Followers see both on the home feed.")
    follow_rows, song_rows = [], []
    listened = {}  # user_id -> [artist_id]
    for artist_id, listeners in follows.items():
        for user_id in listeners:
            listened.setdefault(user_id, []).append(artist_id)
    for user_id in fan_ids:
        for other in rng.sample([f for f in fan_ids if f != user_id], min(len(fan_ids) - 1, rng.randint(2, 6))):
            follow_rows.append((user_id, other, now - datetime.timedelta(days=rng.uniform(0, 120))))
        if rng.random() < 0.6 and listened.get(user_id):
            picks = []
            for artist_id in listened[user_id]:
                for album_id, tracks in catalog[artist_id]["albums"]:
                    picks.extend((artist_id, album_id, track_id) for track_id in tracks)
            for position, (artist_id, album_id, track_id) in enumerate(rng.sample(picks, min(len(picks), rng.randint(1, 5))), 1):
                track = next(t for t in track_rows if t[0] == track_id)
                album = next(a for a in album_rows if a[0] == album_id)
                spotify_id = track[5] if not track[5].startswith("seed:") else \
                    "".join(rng.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZabcdefghjkmnpqrstvwxyz") for _ in range(22))
                song_rows.append((user_id, position, spotify_id, track[2], artist_id, album[2], album[7],
                                  now - datetime.timedelta(days=rng.uniform(0, 60))))
    sql.append(insert("follows", ["follower_id", "followee_id", "created_at"], follow_rows))

    # --- today's music prompt (0025): about a third of fans have answered it -------------------------------------
    answers = ["The Velvet Ropes", "Face Value Riot", "Encore Envy", "The Soundcheck Saints", "Front Row Ghosts",
               "B-Side Bandits", "Merch Table Prophets", "The Setlist Society", "Lost in the Pit", "Feedback Loop Club",
               "The Green Room Kids", "Second Encore", "Wristband Theory", "Late Night Liner Notes", "Static Hearts"]
    answer_values = ",\n  ".join(
        f"({lit(user_id)}::uuid, {lit(rng.choice(answers))}, {lit('followers' if rng.random() < 0.2 else 'public')})"
        for user_id in fan_ids if rng.random() < 0.33)
    if answer_values:
        sql.append("-- Today's prompt is the band-name one (as staff would schedule it), and answers to it.\n"
                   "INSERT INTO prompts (day, text, source, created_by) VALUES ((now() AT TIME ZONE 'UTC')::date, "
                   f"'What''s your best hypothetical band name?', 'staff', {lit(staff_id)});\n"
                   "INSERT INTO prompt_answers (prompt_id, user_id, body, visibility)\n"
                   "SELECT (prompt_for_day((now() AT TIME ZONE 'UTC')::date)).id, a.u, a.body, a.v::visibility FROM (VALUES\n  "
                   + answer_values + ") AS a (u, body, v);")
    if song_rows:
        sql.append(insert("grab_bag_songs", ["user_id", "position", "spotify_track_id", "title", "artist_id", "album_title", "cover_url",
                                             "added_at"], song_rows))

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
 WHERE artist_id IN (SELECT id FROM artists WHERE spotify_artist_id = ANY(%SEEDED_IDS%))
ON CONFLICT (user_id, artist_id) DO UPDATE
  SET raw_score = EXCLUDED.raw_score, percentile = EXCLUDED.percentile, is_estimated = EXCLUDED.is_estimated,
      baseline_weight = EXCLUDED.baseline_weight, breakdown = EXCLUDED.breakdown, computed_at = now();
SELECT request_full_rescore() AS rescore_jobs_queued;""".replace("%SEEDED_IDS%", lit(seeded_ids)))

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
UNION ALL SELECT 'fans selected by the draw', count(*) FROM allocation_registrations WHERE outcome = 'selected'
UNION ALL SELECT 'follows', count(*) FROM follows
UNION ALL SELECT 'grab-bag songs', count(*) FROM grab_bag_songs
UNION ALL SELECT 'shared photos', count(*) FROM submissions WHERE shared_at IS NOT NULL
UNION ALL SELECT 'photo files', count(*) FROM submission_files
UNION ALL SELECT 'prompt answers today', count(*) FROM prompt_answers;""")
    examples = [u[1] for u in users[1:6]]
    return "\n\n".join(sql) + "\n", examples, files


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fans", type=int, default=200)
    parser.add_argument("--artists", type=int, default=8, help=f"up to {len(ARTISTS)}")
    parser.add_argument("--shows", type=int, default=10)
    parser.add_argument("--seed", type=int, default=1, help="random seed; the same seed gives the same data")
    parser.add_argument("--wipe", action="store_true", help="run testScripts/wipeLocalDb.sh first (it asks)")
    parser.add_argument("--sql", action="store_true", help="print the SQL instead of running it")
    parser.add_argument("--fake", action="store_true", help="made-up artists even when Spotify credentials are set")
    parser.add_argument("--storage", default=None,
                        help="where to write the placeholder photo files (the API's Storage__LocalPath; "
                             "default services/api/.local-storage)")
    args = parser.parse_args()
    if not 1 <= args.artists <= min(len(ARTISTS), len(REAL_ARTISTS)):
        sys.exit(f"--artists must be between 1 and {min(len(ARTISTS), len(REAL_ARTISTS))}.")
    if args.fans < 1 or args.shows < 1:
        sys.exit("--fans and --shows must be at least 1.")

    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
    credentials = None if args.fake else spotify_credentials(root)
    real = None
    if credentials:
        print(f"Fetching {args.artists} real artists from Spotify...", file=sys.stderr)
        try:
            real = fetch_real_catalog(REAL_ARTISTS[:args.artists], credentials)
        except (urllib.error.URLError, KeyError, ValueError) as e:
            sys.exit(f"Couldn't read the artists from Spotify ({e}). Check Spotify__ClientId and Spotify__ClientSecret, or use --fake.")
    else:
        print("No Spotify credentials (Spotify__ClientId, Spotify__ClientSecret): seeding made-up artists.", file=sys.stderr)
    sql, examples, files = generate(args.fans, args.artists, args.shows, args.seed, real)
    if args.sql:
        sys.stdout.write(sql)
        return

    storage = args.storage or os.path.join(root, "services", "api", ".local-storage")
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
    write_photo_files(storage, files)
    print(f"\nSeeded. Sign in from the app with dev sign-in as dev_staff (staff) or a fan: {', '.join(examples)}, ...")
    print(f"Wrote {len(files)} placeholder photo files under {storage} (the API's Storage__LocalPath).")
    print("Start the worker (dotnet run --project workers) to replace the provisional scores with the real ones.")


def write_photo_files(storage, files):
    """Writes the placeholder photos where the API's LocalFileStore reads them; an earlier seed's files are removed."""
    quarantine = os.path.join(storage, "quarantine")
    os.makedirs(quarantine, exist_ok=True)
    for name in os.listdir(quarantine):
        if name.startswith("seed-") and name.endswith(".png"):
            os.remove(os.path.join(quarantine, name))
    for key, content in files:
        with open(os.path.join(storage, *key.split("/")), "wb") as handle:
            handle.write(content)


if __name__ == "__main__":
    main()
