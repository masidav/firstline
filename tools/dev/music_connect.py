#!/usr/bin/env python3
"""Connect a real Apple Music or Tidal account to the LOCAL API, without the mobile app. Local development only.

Like spotify_connect.py (which this reuses for the dev session and the API calls): creates a dev user and session in
the local docker-compose database, signs an access token with your Auth__SigningKey, runs the provider's authorization
in your browser, catches the result on 127.0.0.1, calls PUT /v1/me/music/{provider} exactly as the app does, waits for
the worker's sync, and prints what was stored. This is the check ADR 0007 lists as "Not verified": run it once per
provider before any fan connects.

Setup (once):
  - .env: Spotify__TokenKey (stored tokens use it) and the MusicProviders__* settings (.env.example), with
    MusicProviders__Tidal__RedirectUris including http://127.0.0.1:8889/callback.
  - Tidal dashboard: register http://127.0.0.1:8889/callback. Apple Developer account: a MusicKit key.
  - Start the database, API, and worker (docs/DEVELOPMENT.md), each with `set -a; . ./.env; set +a`.

Usage (from the repo root, with .env exported the same way):
  python3 tools/dev/music_connect.py tidal connect
  python3 tools/dev/music_connect.py apple-music connect     # opens a page that runs MusicKit JS in your browser
  python3 tools/dev/music_connect.py tidal status

Stdlib only. Never prints tokens or codes.
"""

import argparse
import base64
import hashlib
import http.server
import json
import os
import secrets
import sys
import time
import urllib.parse
import webbrowser

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from spotify_connect import api, dev_session, env, psql  # noqa: E402

# Same as TidalOptions.RequiredScopes (packages/music-providers/MusicProviderOptions.cs).
TIDAL_SCOPES = "user.read collection.read playlists.read"
TIDAL_AUTHORIZE = "https://login.tidal.com/authorize"
DEFAULT_TIDAL_REDIRECT = "http://127.0.0.1:8889/callback"
DEFAULT_APPLE_PORT = 8890

# What the page in the browser does for Apple Music: MusicKit JS with the API's developer token, then the user
# token is posted back here. Same calls as apps/mobile/src/features/music/appleMusicConnect.ts.
APPLE_PAGE = """<!doctype html>
<meta charset="utf-8"><title>Connect Apple Music (local dev)</title>
<body style="font-family: system-ui; max-width: 36em; margin: 3em auto">
<h1>Connect Apple Music</h1>
<p id="status">Loading MusicKit…</p>
<button id="go" disabled>Sign in to Apple Music</button>
<script src="https://js-cdn.music.apple.com/musickit/v3/musickit.js" async></script>
<script>
const status = document.getElementById('status'), go = document.getElementById('go');
document.addEventListener('musickitloaded', async () => {
  try {
    await MusicKit.configure({ developerToken: %(developer_token)s, app: { name: 'FirstLine (local dev)', build: '0.1.0' } });
    status.textContent = 'MusicKit ready.';
    go.disabled = false;
  } catch (e) { status.textContent = 'MusicKit failed to configure: ' + e; }
});
go.onclick = async () => {
  go.disabled = true;
  try {
    const token = await MusicKit.getInstance().authorize();
    const r = await fetch('/token', { method: 'POST', headers: { 'Content-Type': 'application/json' },
                                      body: JSON.stringify({ state: %(state)s, music_user_token: token }) });
    status.textContent = r.ok ? 'Done. You can close this tab and return to the terminal.' : 'The local script refused the token.';
  } catch (e) { status.textContent = 'Not authorized: ' + e; go.disabled = false; }
};
</script>
"""


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def serve_until(port, handler_class, timeout_seconds, result):
    server = http.server.HTTPServer(("127.0.0.1", port), handler_class)
    server.timeout = 5
    deadline = time.time() + timeout_seconds
    while not result and time.time() < deadline:
        server.handle_request()
    server.server_close()


def tidal_authorize(client_id, redirect_uri, open_browser):
    """Runs Tidal's PKCE authorization in the browser and returns (code, verifier)."""
    parsed = urllib.parse.urlparse(redirect_uri)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or not parsed.port:
        sys.exit("--redirect-uri must be http://127.0.0.1:<port>/<path>.")
    verifier = b64url(secrets.token_bytes(32))
    challenge = b64url(hashlib.sha256(verifier.encode()).digest())
    state = secrets.token_urlsafe(16)
    url = TIDAL_AUTHORIZE + "?" + urllib.parse.urlencode({
        "response_type": "code", "client_id": client_id, "redirect_uri": redirect_uri, "scope": TIDAL_SCOPES,
        "code_challenge_method": "S256", "code_challenge": challenge, "state": state,
    })
    result = {}

    class Callback(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802
            target = urllib.parse.urlparse(self.path)
            query = urllib.parse.parse_qs(target.query)
            if target.path != parsed.path or query.get("state", [""])[0] != state:
                self.send_response(400)
                self.end_headers()
                self.wfile.write(b"Unexpected request; use the link printed in the terminal.\n")
                return
            result["error" if "error" in query else "code"] = query.get("error", query.get("code", [""]))[0]
            self.send_response(200)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.end_headers()
            self.wfile.write(b"Done. You can close this tab and return to the terminal.\n")

        def log_message(self, *args):  # keep the code out of the terminal
            pass

    print("Opening Tidal in your browser. If it does not open, visit:\n  " + url)
    if open_browser:
        webbrowser.open(url)
    serve_until(parsed.port, Callback, 300, result)
    if not result:
        sys.exit("Timed out after 5 minutes waiting for Tidal's redirect.")
    if "error" in result:
        sys.exit(f"Tidal authorization failed: {result['error']}")
    return result["code"], verifier


def apple_authorize(developer_token, port, open_browser):
    """Serves a MusicKit JS page on 127.0.0.1 and returns the Music User Token it posts back."""
    state = secrets.token_urlsafe(16)
    page = (APPLE_PAGE % {"developer_token": json.dumps(developer_token), "state": json.dumps(state)}).encode()
    result = {}

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802
            if urllib.parse.urlparse(self.path).path != "/":
                self.send_response(404)
                self.end_headers()
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.end_headers()
            self.wfile.write(page)

        def do_POST(self):  # noqa: N802
            length = int(self.headers.get("Content-Length", "0"))
            try:
                body = json.loads(self.rfile.read(length) or b"{}")
            except json.JSONDecodeError:
                body = {}
            token = body.get("music_user_token")
            if self.path != "/token" or body.get("state") != state or not isinstance(token, str) or not token:
                self.send_response(400)
                self.end_headers()
                return
            result["token"] = token
            self.send_response(204)
            self.end_headers()

        def log_message(self, *args):
            pass

    url = f"http://127.0.0.1:{port}/"
    print("Opening the Apple Music page in your browser. If it does not open, visit:\n  " + url)
    if open_browser:
        webbrowser.open(url)
    serve_until(port, Handler, 300, result)
    if not result:
        sys.exit("Timed out after 5 minutes waiting for the Apple Music authorization.")
    return result["token"]


def get_status(base, token, provider):
    status_code, status = api("GET", base, f"/v1/me/music/{provider}", token)
    if status_code != 200:
        sys.exit(f"GET /v1/me/music/{provider} returned {status_code}:\n{json.dumps(status, indent=2)}\n"
                 "A 401 usually means Auth__SigningKey here differs from the API's.")
    return status


def print_status(status):
    print(f"  provider: {status.get('provider')}   connected: {status.get('connected')}   sync_state: {status.get('sync_state')}"
          f"   sync_error: {status.get('sync_error')}")
    print(f"  last_synced_at: {status.get('last_synced_at')}   tracked_artists_found: {status.get('tracked_artists_found')}")


def print_signals(user_id, provider):
    rows = psql(
        """
        SELECT a.name, s.liked_tracks, s.playlist_tracks, s.distinct_albums, COALESCE(s.top_artist_rank_short::text, '-'),
               s.recent_plays, s.followed
        FROM user_artist_signals s JOIN artists a ON a.id = s.artist_id
        WHERE s.user_id = :'user_id' AND s.provider = :'provider' ORDER BY a.name;
        """,
        user_id=user_id, provider=provider)
    if not rows:
        print(f"  No {provider} signals stored: nothing in the library matched a tracked, active artist by name"
              " (tools/dev/spotify_connect.py track-artist adds one; artist names must match the provider's).")
        return
    print(f"  {'artist':30} {'liked':>6} {'playlist':>9} {'albums':>7} {'heavy':>6} {'recent':>7} {'follow':>7}")
    for name, liked, playlist, albums, rank, recent, followed in rows:
        print(f"  {name[:30]:30} {liked:>6} {playlist:>9} {albums:>7} {rank:>6} {recent:>7} {followed:>7}")
    learned = psql("SELECT count(*) FROM tracks WHERE isrc IS NOT NULL")[0][0]
    print(f"  tracks with an ISRC in the catalog now: {learned}")


def wait_for_sync(args, token, provider):
    deadline = time.time() + args.wait
    waited_queued = 0
    status = get_status(args.api, token, provider)
    while time.time() < deadline and status["sync_state"] in ("queued", "running"):
        waited_queued = waited_queued + 3 if status["sync_state"] == "queued" else 0
        if waited_queued == 30:
            print("  Still queued after 30 s. Is the worker running (`dotnet run --project workers`)?")
        time.sleep(3)
        status = get_status(args.api, token, provider)
    return status


def connect(args):
    env("Auth__SigningKey")
    env("Spotify__TokenKey")
    user_id, token = dev_session(args.handle)
    if args.provider == "tidal":
        client_id = env("MusicProviders__Tidal__ClientId")
        allowed = [u.strip() for u in os.environ.get("MusicProviders__Tidal__RedirectUris", "").split(",")]
        if args.redirect_uri not in allowed:
            sys.exit(f"Add {args.redirect_uri} to MusicProviders__Tidal__RedirectUris (and restart the API), and register"
                     " it in the Tidal developer dashboard.")
        code, verifier = tidal_authorize(client_id, args.redirect_uri, not args.no_browser)
        body = {"code": code, "code_verifier": verifier, "redirect_uri": args.redirect_uri}
    else:
        status_code, developer = api("GET", args.api, "/v1/me/music/apple_music/developer-token", token)
        if status_code != 200:
            sys.exit(f"GET /v1/me/music/apple_music/developer-token returned {status_code}:\n{json.dumps(developer, indent=2)}\n"
                     "Set MusicProviders__AppleMusic__TeamId, KeyId, and PrivateKey in .env and restart the API.")
        body = {"music_user_token": apple_authorize(developer["token"], args.port, not args.no_browser)}

    status_code, result = api("PUT", args.api, f"/v1/me/music/{args.provider}", token, body)
    if status_code != 200:
        sys.exit(f"PUT /v1/me/music/{args.provider} returned {status_code}:\n{json.dumps(result, indent=2)}")
    print(f"Connected {args.provider} for dev user {args.handle}. Waiting for the worker to sync...")
    print_status(wait_for_sync(args, token, args.provider))
    print_signals(user_id, args.provider)


def status_command(args):
    user_id, token = dev_session(args.handle)
    print_status(get_status(args.api, token, args.provider))
    print_signals(user_id, args.provider)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--api", default="http://localhost:5271", help="local API base URL")
    parser.add_argument("--handle", default="dev_fan", help="local dev user to act as")
    parser.add_argument("provider", choices=["tidal", "apple-music"])
    commands = parser.add_subparsers(dest="command", required=True)

    c = commands.add_parser("connect", help="connect your account and wait for the first sync")
    c.add_argument("--redirect-uri", default=DEFAULT_TIDAL_REDIRECT, help="Tidal only")
    c.add_argument("--port", type=int, default=DEFAULT_APPLE_PORT, help="Apple Music only: local port for the MusicKit page")
    c.add_argument("--no-browser", action="store_true", help="print the URL instead of opening a browser")
    c.add_argument("--wait", type=int, default=180, help="seconds to wait for the sync")
    c.set_defaults(run=connect)

    s = commands.add_parser("status", help="show the connection and stored signals")
    s.set_defaults(run=status_command)

    args = parser.parse_args()
    args.provider = "apple_music" if args.provider == "apple-music" else args.provider
    args.run(args)


if __name__ == "__main__":
    main()
