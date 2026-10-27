#!/usr/bin/env python3
"""Create test shows and fan allocations on the LOCAL API, as a dev staff user. Local development only.

Uses the staff endpoints (/v1/admin/events, /v1/admin/events/{id}/allocations, .../publish) exactly as a staff tool
would, signed in as the dev user `dev_staff` (created in the local docker-compose database and made staff).

Usage (from the repo root, with .env exported: set -a; . ./.env; set +a):
  python3 tools/dev/allocations.py artists                      # active artists you can make shows for
  python3 tools/dev/allocations.py cities chicago                # hub city ids
  python3 tools/dev/allocations.py create --artist Geese --city us-il-chicago --in-days 14 \\
      --tickets 50 --price 45 --opens-in-hours 0 --closes-in-days 3   # --min-raw-score N to override
  python3 tools/dev/allocations.py show <event_id>
  python3 tools/dev/allocations.py close <allocation_id>        # end registration now (local database only)
  python3 tools/dev/allocations.py draw <allocation_id>         # run the draw and print the ranking

`create` publishes the allocation unless --draft is given. --opens-in-hours 0 opens registration right away.
Stdlib only.
"""

import argparse
import datetime
import json
import os
import sys
import time
import urllib.parse

sys.path.insert(0, os.path.dirname(__file__))
from spotify_connect import api, dev_session, psql  # noqa: E402 (shared dev helpers)

STAFF = "dev_staff"


def staff_token(base):
    user_id, token = dev_session(STAFF)
    psql("UPDATE users SET is_staff = true WHERE id = :'id'", id=user_id)
    return token


def call(method, base, path, token, body=None, expect=(200, 201)):
    status, data = api(method, base, path, token, body)
    if status not in expect:
        sys.exit(f"{method} {path} failed with {status}: {json.dumps(data, indent=2)}")
    return data


def at(**delta):
    return (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(**delta)).isoformat(timespec="seconds")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--api", default=os.environ.get("API_URL", "http://127.0.0.1:5271"))
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("artists", help="list active artists")
    cities = commands.add_parser("cities", help="search hub cities")
    cities.add_argument("query")
    create = commands.add_parser("create", help="add a show with a fan allocation")
    create.add_argument("--artist", required=True, help="artist name (exact, case-insensitive) or id")
    create.add_argument("--city", required=True, help="hub city id, e.g. us-il-chicago")
    create.add_argument("--name", help="show name (default: '<artist> live')")
    create.add_argument("--venue")
    create.add_argument("--in-days", type=float, default=14, help="show starts this many days from now")
    create.add_argument("--tickets", type=int, default=50)
    create.add_argument("--price", type=float, default=45, help="face value in dollars")
    create.add_argument("--currency", default="USD")
    create.add_argument("--min-raw-score", type=float, help="threshold; omit for the two-saved-albums default")
    create.add_argument("--opens-in-hours", type=float, default=0)
    create.add_argument("--closes-in-days", type=float, default=3)
    create.add_argument("--draft", action="store_true", help="leave the allocation unpublished")
    show = commands.add_parser("show", help="print a show with all its allocations")
    show.add_argument("event_id")
    close = commands.add_parser("close", help="end an allocation's registration now (local database only)")
    close.add_argument("allocation_id")
    draw = commands.add_parser("draw", help="run the draw for a closed allocation and print the ranking")
    draw.add_argument("allocation_id")
    args = parser.parse_args()

    if args.command == "artists":
        for artist_id, name in psql("SELECT id, name FROM artists WHERE status = 'active' ORDER BY lower(name)"):
            print(f"  {artist_id}  {name}")
        return

    token = staff_token(args.api)
    if args.command == "cities":
        for city in call("GET", args.api, f"/v1/cities?q={urllib.parse.quote(args.query)}", token)["items"]:
            print(f"  {city['id']:28} {city['name']}, {city['region']}")
        return
    if args.command == "show":
        print(json.dumps(call("GET", args.api, f"/v1/admin/events/{args.event_id}", token), indent=2))
        return
    if args.command == "close":
        # The API never lets an open allocation's window change, so this edits the local database directly.
        psql("""
             ALTER TABLE allocations DISABLE TRIGGER trg_allocation_check;
             UPDATE allocations SET registration_opens_at = LEAST(registration_opens_at, now() - interval '2 minutes'),
                                    registration_closes_at = now() - interval '1 minute'
              WHERE id = :'a'::uuid;
             ALTER TABLE allocations ENABLE TRIGGER trg_allocation_check;
             """, a=args.allocation_id)
        print(f"Registration for {args.allocation_id} is closed. Run `draw` next.")
        return
    if args.command == "draw":
        # The draw runs in the worker (allocations.select): queue it, then poll until it is done.
        draw = call("POST", args.api, f"/v1/admin/allocations/{args.allocation_id}/selection", token)
        waited = 0
        while draw["status"] != "done":
            if draw["status"] == "failed":
                sys.exit(f"The draw failed: {draw['error']}. Run `draw` again to retry.")
            if waited == 0:
                print("Draw queued; waiting for the worker (dotnet run --project workers) ...")
            if waited > 120:
                sys.exit("The worker hasn't run the draw in two minutes. Is it running?")
            time.sleep(2)
            waited += 2
            draw = call("GET", args.api, f"/v1/admin/allocations/{args.allocation_id}/selection", token)
        selection = draw["selection"]
        print(f"Draw (seed {selection['seed']}): {selection['selected_fans']} fans selected, "
              f"{selection['seats_assigned']}/{selection['tickets']} seats")
        for item in call("GET", args.api, f"/v1/admin/allocations/{args.allocation_id}/registrations", token)["items"]:
            r = item["registration"]
            print(f"  {r['selection_rank'] or '-':>4}  {item['handle']:24} {r['status']:13} seats {r['seats'] or 0}"
                  f"  raw {r['raw_score']}  {'local' if r['local'] else 'not local'}{'  +1' if r['plus_one'] else ''}")
        return

    rows = psql("SELECT id, name FROM artists WHERE (id::text = :'a' OR lower(name) = lower(:'a')) AND status <> 'paused'",
                a=args.artist)
    if len(rows) != 1:
        sys.exit(f"Found {len(rows)} artists matching {args.artist!r}. Use `artists` to list them, or pass the id.")
    artist_id, artist_name = rows[0]
    detail = call("POST", args.api, "/v1/admin/events", token, {
        "artist_id": artist_id, "name": args.name or f"{artist_name} live", "venue": args.venue,
        "city_id": args.city, "starts_at": at(days=args.in_days)})
    event_id = detail["event"]["id"]
    allocation = call("POST", args.api, f"/v1/admin/events/{event_id}/allocations", token, {
        "tickets": args.tickets, "face_value_cents": round(args.price * 100), "currency": args.currency,
        "registration_opens_at": at(hours=args.opens_in_hours, seconds=-5),
        "registration_closes_at": at(days=args.closes_in_days),
        **({} if args.min_raw_score is None else {"min_raw_score": args.min_raw_score})})
    if not args.draft:
        allocation = call("POST", args.api, f"/v1/admin/allocations/{allocation['id']}/publish", token)
    print(f"Show {event_id}: {detail['event']['name']} in {detail['event']['city']}")
    print(f"Allocation {allocation['id']}: {allocation['tickets']} tickets, status {allocation['status']}, "
          f"threshold {allocation['min_raw_score']}, registration until {allocation['registration_closes_at']}")


if __name__ == "__main__":
    main()
