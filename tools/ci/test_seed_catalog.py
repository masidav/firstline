"""The dev seed's real-artist path (tools/dev/seed.py) against a faked Spotify: no network, no credentials."""
import io
import json
import os
import sys
import unittest
import urllib.error

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "dev"))
import seed  # noqa: E402


class FakeResponse(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class FakeSpotify:
    """Answers the four requests fetch_real_catalog makes; records them."""

    def __init__(self):
        self.calls = []
        self.rate_limited_once = False

    def __call__(self, request, timeout=None):
        url = request.full_url
        self.calls.append((request.get_method(), url, request.headers.get("Authorization")))
        if url == seed.SPOTIFY_TOKEN_URL:
            return FakeResponse(json.dumps({"access_token": "app-1", "expires_in": 3600}).encode())
        path = url[len(seed.SPOTIFY_API):]
        if path.startswith("search?") and not self.rate_limited_once:
            self.rate_limited_once = True
            raise urllib.error.HTTPError(url, 429, "Too Many Requests", {"Retry-After": "0"}, None)
        if path.startswith("search?") and "Nobody" in path:
            body = {"artists": {"items": []}}
        elif path.startswith("search?"):
            body = {"artists": {"items": [
                {"id": "0aaaaaaaaaaaaaaaaaaaaa", "name": "Geese Tribute", "images": []},
                {"id": "066X20Nz7iquqkkCW6Jxy6", "name": "Geese", "images": [
                    {"url": "https://i.scdn.co/geese-640", "width": 640}, {"url": "https://i.scdn.co/geese-320", "width": 320},
                    {"url": "https://i.scdn.co/geese-160", "width": 160}]}]}}
        elif path.startswith("artists/066X20Nz7iquqkkCW6Jxy6/albums"):
            body = {"items": [
                {"id": "1albumaaaaaaaaaaaaaaaa", "name": "Getting Killed", "release_date": "2025-09-26", "release_date_precision": "day",
                 "images": [{"url": "https://i.scdn.co/gk-300", "width": 300}, {"url": "https://i.scdn.co/gk-640", "width": 640}]},
                {"id": "2albumaaaaaaaaaaaaaaaa", "name": "Getting Killed", "release_date": "2025", "release_date_precision": "year",
                 "images": []},
                {"id": "3albumaaaaaaaaaaaaaaaa", "name": "3D Country", "release_date": "2023-06", "release_date_precision": "month",
                 "images": []}]}
        elif path.startswith("albums/"):
            body = {"items": [{"id": "1trackaaaaaaaaaaaaaaaa", "name": "Trinidad", "track_number": 1},
                              {"id": None, "name": "A local file", "track_number": 2}]}
        else:
            raise AssertionError(f"unexpected request {path}")
        return FakeResponse(json.dumps(body).encode())


class RealCatalogTests(unittest.TestCase):
    def test_fetches_the_exact_artist_their_newest_distinct_albums_and_tracks(self):
        spotify = FakeSpotify()
        catalog = seed.fetch_real_catalog(["Geese", "Nobody Here"], ("client", "secret"), albums_per_artist=4, opener=spotify)

        self.assertEqual(1, len(catalog))   # "Nobody Here" isn't on Spotify: skipped
        geese = catalog[0]
        self.assertEqual(("Geese", "066X20Nz7iquqkkCW6Jxy6", "https://i.scdn.co/geese-320", True),
                         (geese["name"], geese["spotify_id"], geese["image_url"], geese["real"]))
        self.assertEqual(["Getting Killed", "3D Country"], [a["title"] for a in geese["albums"]])   # reissue dropped
        first = geese["albums"][0]
        self.assertEqual(("https://i.scdn.co/gk-640", "day"), (first["cover_url"], first["precision"]))
        self.assertEqual("month", geese["albums"][1]["precision"])
        self.assertEqual([("Trinidad", "1trackaaaaaaaaaaaaaaaa", 1)],
                         [(t["title"], t["spotify_id"], t["track_number"]) for t in first["tracks"]])
        token_call = spotify.calls[0]
        self.assertEqual(("POST", seed.SPOTIFY_TOKEN_URL), token_call[:2])
        self.assertTrue(token_call[2].startswith("Basic "))
        self.assertTrue(all(c[2] == "Bearer app-1" for c in spotify.calls[1:]))
        self.assertTrue(any("include_groups=album" in c[1] and "limit=10" in c[1] for c in spotify.calls))

    def test_release_dates_keep_their_precision(self):
        self.assertEqual((seed.datetime.date(2023, 6, 1), "month"), seed.parse_release("2023-06", None))
        self.assertEqual((seed.datetime.date(1981, 1, 1), "year"), seed.parse_release("1981", "year"))

    def test_generate_uses_real_ids_covers_and_track_ids_for_grab_bags(self):
        catalog = seed.fetch_real_catalog(["Geese"], ("client", "secret"), opener=FakeSpotify())
        sql, _, _ = seed.generate(20, 1, 2, 1, catalog)
        self.assertIn("'066X20Nz7iquqkkCW6Jxy6'", sql)
        self.assertIn("'https://i.scdn.co/gk-640'", sql)
        self.assertNotIn("'seed:", sql.split("INSERT INTO artists", 1)[1])   # the guard above it may say 'seed:%'
        self.assertIn("'1trackaaaaaaaaaaaaaaaa'", sql.split("INSERT INTO grab_bag_songs", 1)[1])

    def test_without_a_real_catalog_artists_are_made_up(self):
        sql, _, _ = seed.generate(20, 2, 2, 1)
        self.assertIn("'seed:glass-harbor'", sql)


if __name__ == "__main__":
    unittest.main()
