#!/usr/bin/env python3
"""Connect a real Spotify account to the LOCAL API, without the mobile app. Local development only.

Signing in normally needs Apple or Google, so this script creates a dev user and session directly in the local
docker-compose database and signs an access token with your Auth__SigningKey. It then runs Spotify's
authorization (PKCE) in your browser, catches the redirect on 127.0.0.1, and calls PUT /v1/me/spotify exactly
as the app will. Finally it waits for the worker's sync and prints what was stored.

Setup (once):
  - Spotify dashboard: add the redirect URI http://127.0.0.1:8888/callback, and your Spotify account under Users.
  - .env: Spotify__ClientId, Spotify__ClientSecret, Spotify__TokenKey, and Spotify__RedirectUris including
    http://127.0.0.1:8888/callback.
  - Start the database, API, and worker (docs/DEVELOPMENT.md), each with `set -a; . ./.env; set +a`.

Usage (from the repo root, with .env exported the same way):
  python3 tools/dev/spotify_connect.py search radiohead
  python3 tools/dev/spotify_connect.py track-artist 4Z8W4fKeB5YxbusRsdQVPb
  python3 tools/dev/spotify_connect.py connect
  python3 tools/dev/spotify_connect.py status

Stdlib only. Never prints tokens or the Spotify code.
"""

import argparse
import base64
import hashlib
import hmac
import http.server
import json
import os
import re
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import webbrowser

# Same as SpotifyOptions.RequiredScopes (packages/spotify/SpotifyOptions.cs): the API rejects a connection missing any.
SCOPES = " ".join([
    "user-library-read", "user-top-read", "user-read-recently-played", "playlist-read-private",
    "playlist-read-collaborative", "user-follow-read", "user-read-playback-state", "user-modify-playback-state",
])
DEFAULT_REDIRECT = "http://127.0.0.1:8888/callback"
COMPOSE_FILE = os.path.join(os.path.dirname(__file__), "..", "..", "infrastructure", "docker-compose.yml")


def env(name):
    value = os.environ.get(name, "")
    if not value:
        sys.exit(f"{name} is not set. Export .env first: set -a; . ./.env; set +a")
    return value


def psql(sql, **variables):
    """Runs SQL in the local compose database (DEV_DATABASE, default fandom_score). Values are passed as psql
    variables (use :'name'), never pasted in."""
    command = ["docker", "compose", "-f", COMPOSE_FILE, "exec", "-T", "postgres",
               "psql", "-U", "fandom", "-d", os.environ.get("DEV_DATABASE", "fandom_score"), "-v", "ON_ERROR_STOP=1", "-q", "-At", "-F", "\t"]
    for name, value in variables.items():
        command += ["-v", f"{name}={value}"]
    result = subprocess.run(command, input=sql, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"Database command failed (is the compose database running?):\n{result.stderr.strip()}")
    return [line.split("\t") for line in result.stdout.splitlines() if line]


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def access_token(user_id, session_id):
    """An HS256 access token like SessionService issues (ADR 0003)."""
    key = base64.b64decode(env("Auth__SigningKey"))
    now = int(time.time())
    header = {"alg": "HS256", "typ": "JWT"}
    claims = {
        "iss": os.environ.get("Auth__Issuer", "fandom-score"),
        "aud": os.environ.get("Auth__Audience", "fandom-score-api"),
        "sub": user_id, "sid": session_id, "iat": now, "nbf": now, "exp": now + 900,
    }
    signing_input = f"{b64url(json.dumps(header).encode())}.{b64url(json.dumps(claims).encode())}"
    signature = hmac.new(key, signing_input.encode(), hashlib.sha256).digest()
    return f"{signing_input}.{b64url(signature)}"


def dev_session(handle):
    if not re.fullmatch(r"[A-Za-z0-9_]{3,30}", handle):
        sys.exit("Handle: 3 to 30 letters, digits, or underscores.")
    rows = psql(
        """
        INSERT INTO users (handle, email) VALUES (:'handle', :'handle' || '@dev.localhost')
        ON CONFLICT (handle) DO NOTHING;
        INSERT INTO auth_sessions (user_id, expires_at)
        SELECT id, now() + interval '1 day' FROM users WHERE handle = :'handle' AND deleted_at IS NULL
        RETURNING user_id, id;
        """,
        handle=handle)
    if not rows:
        sys.exit(f"Could not create a session for {handle}.")
    user_id, session_id = rows[0]
    return user_id, access_token(user_id, session_id)


def api(method, base, path, token, body=None):
    request = urllib.request.Request(
        base.rstrip("/") + path, method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        raw = e.read()
        return e.code, json.loads(raw) if raw else None
    except urllib.error.URLError as e:
        sys.exit(f"Cannot reach the API at {base} ({e.reason}). Is `dotnet run --project services/api` running?")


def authorize(client_id, redirect_uri, open_browser):
    """Runs Spotify's PKCE authorization in the browser and returns (code, verifier)."""
    parsed = urllib.parse.urlparse(redirect_uri)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or not parsed.port:
        sys.exit("--redirect-uri must be http://127.0.0.1:<port>/<path> (Spotify allows http only for loopback IPs).")

    verifier = b64url(secrets.token_bytes(32))
    challenge = b64url(hashlib.sha256(verifier.encode()).digest())
    state = secrets.token_urlsafe(16)
    url = "https://accounts.spotify.com/authorize?" + urllib.parse.urlencode({
        "client_id": client_id, "response_type": "code", "redirect_uri": redirect_uri, "scope": SCOPES,
        "code_challenge_method": "S256", "code_challenge": challenge, "state": state,
    })

    result = {}

    class Callback(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802 (http.server API)
            query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            if urllib.parse.urlparse(self.path).path != parsed.path:
                self.send_response(404)
                self.end_headers()
                return
            if query.get("state", [""])[0] != state:
                # An old tab or a forged request: ignore it and keep waiting for the real redirect.
                self.send_response(400)
                self.send_header("Content-Type", "text/plain; charset=utf-8")
                self.end_headers()
                self.wfile.write(b"Unexpected state; ignored. Use the link printed in the terminal.\n")
                return
            if "error" in query:
                result["error"] = query["error"][0]
            else:
                result["code"] = query.get("code", [""])[0]
            self.send_response(200)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.end_headers()
            self.wfile.write(b"Done. You can close this tab and return to the terminal.\n")

        def log_message(self, *args):  # keep the code out of the terminal
            pass

    server = http.server.HTTPServer(("127.0.0.1", parsed.port), Callback)
    server.timeout = 5
    print("Opening Spotify in your browser. If it does not open, visit:\n  " + url)
    if open_browser:
        webbrowser.open(url)
    deadline = time.time() + 300
    while not result and time.time() < deadline:
        server.handle_request()
    server.server_close()
    if not result:
        sys.exit("Timed out after 5 minutes waiting for Spotify's redirect.")
    if "error" in result:
        sys.exit(f"Spotify authorization failed: {result['error']}")
    return result["code"], verifier


def get_status(base, token):
    status_code, status = api("GET", base, "/v1/me/spotify", token)
    if status_code != 200:
        sys.exit(f"GET /v1/me/spotify returned {status_code}:\n{json.dumps(status, indent=2)}\n"
                 "A 401 usually means Auth__SigningKey here differs from the API's.")
    return status


def print_status(status):
    print(f"  connected: {status.get('connected')}   sync_state: {status.get('sync_state')}"
          f"   sync_error: {status.get('sync_error')}")
    print(f"  last_synced_at: {status.get('last_synced_at')}   tracked_artists_found: {status.get('tracked_artists_found')}")


def print_signals(user_id):
    rows = psql(
        """
        SELECT a.name, s.liked_tracks, s.playlist_tracks, s.distinct_albums, COALESCE(s.top_artist_rank::text, '-'),
               s.recent_plays
        FROM user_artist_signals s JOIN artists a ON a.id = s.artist_id
        WHERE s.user_id = :'user_id' ORDER BY a.name;
        """,
        user_id=user_id)
    if not rows:
        print("  No signals stored: none of your Spotify activity matched a tracked, active artist."
              " Add one with `track-artist`, then `connect` again or wait for the next sync.")
        return
    print(f"  {'artist':30} {'liked':>6} {'playlist':>9} {'albums':>7} {'top rank':>9} {'recent':>7}")
    for name, liked, playlist, albums, rank, recent in rows:
        print(f"  {name[:30]:30} {liked:>6} {playlist:>9} {albums:>7} {rank:>9} {recent:>7}")


def connect(args):
    client_id = env("Spotify__ClientId")
    env("Auth__SigningKey")
    allowed = [u.strip() for u in os.environ.get("Spotify__RedirectUris", "").split(",")]
    if args.redirect_uri not in allowed:
        sys.exit(f"Add {args.redirect_uri} to Spotify__RedirectUris (and restart the API), and register it in the"
                 " Spotify dashboard.")

    user_id, token = dev_session(args.handle)
    code, verifier = authorize(client_id, args.redirect_uri, not args.no_browser)

    status_code, body = api("PUT", args.api, "/v1/me/spotify", token,
                            {"code": code, "code_verifier": verifier, "redirect_uri": args.redirect_uri})
    if status_code != 200:
        sys.exit(f"PUT /v1/me/spotify returned {status_code}:\n{json.dumps(body, indent=2)}")
    print(f"Connected Spotify for dev user {args.handle}. Waiting for the worker to sync...")

    deadline = time.time() + args.wait
    waited_queued = 0
    while time.time() < deadline:
        status = get_status(args.api, token)
        if status["sync_state"] not in ("queued", "running"):
            break
        waited_queued = waited_queued + 3 if status["sync_state"] == "queued" else 0
        if waited_queued == 30:
            print("  Still queued after 30 s. Is the worker running (`dotnet run --project workers`)?")
        time.sleep(3)
    print_status(status)
    print_signals(user_id)


def status_command(args):
    user_id, token = dev_session(args.handle)
    status = get_status(args.api, token)
    print_status(status)
    print_signals(user_id)


def search(args):
    _, token = dev_session(args.handle)
    query = urllib.parse.urlencode({"q": args.query})
    status_code, body = api("GET", args.api, f"/v1/artists/search?{query}", token)
    if status_code != 200:
        sys.exit(f"GET /v1/artists/search returned {status_code}:\n{json.dumps(body, indent=2)}")
    for artist in body["items"]:
        tracked = artist["status"] if artist["tracked"] else "not tracked"
        print(f"  {artist['spotify_artist_id']}  {artist['name']:<32} {tracked}")


def track_artist(args):
    """Adds an artist through POST /v1/artists (name and image from Spotify), then activates it with a placeholder
    baseline, since onboarding is not built yet."""
    if not re.fullmatch(r"[0-9A-Za-z]{22}", args.spotify_artist_id):
        sys.exit("Use the 22-character Spotify artist id (from open.spotify.com/artist/<id>, or `search`).")
    if args.name:
        print("Ignoring the name argument: the API takes the artist's name from Spotify.")
    _, token = dev_session(args.handle)
    status_code, artist = api("POST", args.api, "/v1/artists", token, {"spotify_artist_id": args.spotify_artist_id})
    if status_code not in (200, 201):
        sys.exit(f"POST /v1/artists returned {status_code}:\n{json.dumps(artist, indent=2)}")
    psql(
        """
        INSERT INTO artist_baselines (artist_id, dist_params, proxy_metrics, curve)
        SELECT id, '{}', '{"dev_placeholder": true}', '[]' FROM artists WHERE spotify_artist_id = :'spotify_id'
        ON CONFLICT (artist_id) DO NOTHING;
        UPDATE artists SET status = 'active' WHERE spotify_artist_id = :'spotify_id' AND status = 'onboarding';
        """,
        spotify_id=args.spotify_artist_id)
    print(f"{artist['name']} ({args.spotify_artist_id}) is tracked and active.")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--api", default="http://localhost:5271", help="local API base URL")
    parser.add_argument("--handle", default="dev_fan", help="local dev user to act as")
    commands = parser.add_subparsers(dest="command", required=True)

    c = commands.add_parser("connect", help="connect your Spotify account and wait for the first sync")
    c.add_argument("--redirect-uri", default=DEFAULT_REDIRECT)
    c.add_argument("--no-browser", action="store_true", help="print the URL instead of opening a browser")
    c.add_argument("--wait", type=int, default=180, help="seconds to wait for the sync")
    c.set_defaults(run=connect)

    s = commands.add_parser("status", help="show the connection and stored signals")
    s.set_defaults(run=status_command)

    q = commands.add_parser("search", help="search Spotify for artists through the API")
    q.add_argument("query")
    q.set_defaults(run=search)

    t = commands.add_parser("track-artist", help="add an artist through the API and activate it for testing")
    t.add_argument("spotify_artist_id")
    t.add_argument("name", nargs="?", help=argparse.SUPPRESS)
    t.set_defaults(run=track_artist)

    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
