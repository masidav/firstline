#!/usr/bin/env python3
"""Email a short music-industry brief: hires and exits, concert news, ticketing-industry news.

Pulls the trade press and Google News (the same sources as music_industry_moves.py plus
touring and ticketing outlets), asks Claude to sort each story into a section and to date
the development it reports, keeps only developments from the last 7 days (the event, not
the article), and emails one plain-text message. "No updates." is a valid body.

    python3 industry_newsletter.py --dry-run                 # print the email instead of sending it
    python3 industry_newsletter.py --state newsletter.state.json   # send; later runs skip stories already sent
    python3 industry_newsletter.py --no-llm --dry-run        # keyword sorting and publish dates (testing without credentials)

Sending needs these environment variables (newsletter_cron.sh loads them from
~/.config/industry-newsletter/env):

    NEWSLETTER_TO             recipient (default davidjmasi@gmail.com)
    NEWSLETTER_SMTP_HOST      default smtp.gmail.com
    NEWSLETTER_SMTP_PORT      default 587 (STARTTLS)
    NEWSLETTER_SMTP_USER      the mailbox that sends (for Gmail, the Gmail address)
    NEWSLETTER_SMTP_PASSWORD  for Gmail, an App Password (myaccount.google.com/apppasswords), not the account password
    NEWSLETTER_FROM           default NEWSLETTER_SMTP_USER
    ANTHROPIC_API_KEY         for the Claude step (or an `ant auth login` profile)
"""

from __future__ import annotations

import argparse
import json
import os
import re
import smtplib
import sys
import traceback
from datetime import date, datetime, timedelta, timezone
from email.message import EmailMessage
from email.utils import formatdate, make_msgid

import music_industry_moves as moves

DEFAULT_TO = "davidjmasi@gmail.com"
WINDOW_DAYS = 7
DEFAULT_MODEL = "claude-opus-5-5"

SECTIONS = [
    ("moves", "Hires, exits and layoffs"),
    ("concerts", "Concert news"),
    ("ticketing", "Ticketing industry"),
]

# --------------------------------------------------------------------------------------
# Sources (music_industry_moves.py supplies the hires/exits sources)
# --------------------------------------------------------------------------------------

# name, url, music_filter (see music_industry_moves.FEEDS)
CONCERT_FEEDS = [
    ("Billboard Touring", "https://www.billboard.com/c/music/touring/feed/", False),
    ("Pollstar", "https://news.pollstar.com/feed/", False),
    ("IQ Magazine", "https://www.iq-mag.net/feed/", False),
    ("Rolling Stone Music", "https://www.rollingstone.com/music/music-news/feed/", False),
    ("Stereogum", "https://www.stereogum.com/category/news/feed/", False),
    ("NME", "https://www.nme.com/news/music/feed", False),
    ("BrooklynVegan", "https://www.brooklynvegan.com/feed/", False),
]
CONCERT_QUERIES = [
    '(announces OR announced OR unveils) (tour OR "tour dates" OR "world tour" OR "stadium tour" OR "arena tour" OR residency)',
    '(concert OR tour OR festival OR show) (cancelled OR canceled OR postponed OR cancels OR postpones OR "pulls out")',
    '(festival) (lineup OR "line-up" OR headliners OR headline) 2027',
    '(Coachella OR Glastonbury OR Lollapalooza OR "Bonnaroo" OR "Primavera Sound" OR "Reading and Leeds" OR "Austin City Limits" OR "Governors Ball")',
    '("Sphere" OR "Madison Square Garden" OR "Wembley Stadium" OR "SoFi Stadium" OR "O2 Arena" OR "MetLife Stadium") (concert OR tour OR residency OR shows)',
    'site:pollstar.com (tour OR festival OR venue OR box office)',
    'site:billboard.com (tour OR touring OR festival OR residency OR "box office" OR "Boxscore")',
]

TICKETING_FEEDS = [
    ("TicketNews", "https://www.ticketnews.com/feed/", False),
    ("IQ Magazine", "https://www.iq-mag.net/feed/", False),
    ("Pollstar", "https://news.pollstar.com/feed/", False),
    ("Music Business Worldwide", "https://www.musicbusinessworldwide.com/feed/", False),
    ("Billboard Touring", "https://www.billboard.com/c/music/touring/feed/", False),
]
TICKETING_QUERIES = [
    'Ticketmaster OR "Live Nation" (lawsuit OR FTC OR DOJ OR antitrust OR fees OR ruling OR earnings OR settlement OR fine)',
    'StubHub OR SeatGeek OR "Vivid Seats" OR AXS OR DICE OR Eventbrite OR "Gametime" OR "TickPick" OR "Tixr" OR "See Tickets" OR "Eventim" OR "viagogo"',
    '("ticket resale" OR "secondary ticketing" OR "ticket scalping" OR "ticket bots" OR "BOTS Act" OR "ticket fees" OR "junk fees" OR "all-in pricing" OR "dynamic pricing") (concert OR tickets OR ticketing)',
    '("ticketing" OR "ticket platform" OR "ticket sales") (concert OR music OR tour OR venue) (launch OR raises OR acquires OR acquisition OR partnership OR deal OR regulation OR bill OR law)',
    'site:ticketnews.com',
    'site:iq-mag.net (ticket OR ticketing OR resale)',
]

CONCERT_WORDS = re.compile(
    r"\b(tour|tours|touring|tour dates|residency|residencies|festival|festivals|lineup|line-up|headlin\w*|"
    r"concert|concerts|arena tour|stadium tour|amphitheat\w*|box office|boxscore|"
    r"cancel\w*|postpone\w*|resched\w*|sold out|sells out|on sale|presale|pre-sale|opening act|support act)\b",
    re.I,
)
TICKETING_WORDS = re.compile(
    r"\b(ticket\w*|ticketmaster|live nation|stubhub|seatgeek|vivid seats|eventbrite|tickpick|tixr|"
    r"see tickets|eventim|viagogo|resale|reseller|scalp\w*|bots? act|junk fees?|all-in pricing|dynamic pricing|"
    r"face value|verified fan|presale|pre-sale|on sale|antitrust|ftc|doj|box office)\b",
    re.I,
)
# Brands whose names are ordinary words: only the trade spelling counts (case-sensitive).
TICKETING_BRANDS = re.compile(r"\b(DICE|AXS|Gametime)\b")
NOT_NEWS = re.compile(
    r"\b(review|reviews|reviewed|live review|setlist:|photos:|watch:|listen:|premiere|album review|"
    r"interview|q&a|op-ed|opinion|ranked|best of|the \d+ best|playlist|giveaway|contest|horoscope|"
    r"how to watch|where to watch|live stream|livestream|what time)\b",
    re.I,
)


def looks_like_concert(title: str, summary: str) -> bool:
    if NOT_NEWS.search(title):
        return False
    return bool(CONCERT_WORDS.search(f"{title} {summary[:300]}"))


def looks_like_ticketing(title: str, summary: str) -> bool:
    if NOT_NEWS.search(title):
        return False
    text = f"{title} {summary[:300]}"
    return bool(TICKETING_WORDS.search(text) or TICKETING_BRANDS.search(text))


def fetch_plain(name: str, url: str, since: datetime, keep) -> list[dict]:
    """Like moves.fetch_feed, but with this section's keyword filter instead of the moves one."""
    r = moves.get(url)
    if r is None:
        print(f"  ! {name}: unreachable", file=sys.stderr)
        return []
    import feedparser  # already a dependency of music_industry_moves

    items = []
    for e in feedparser.parse(r.content).entries:
        d = moves.entry_date(e)
        if d and d < since:
            continue
        title = e.get("title", "").strip()
        link = e.get("link", "").strip()
        if not title or not link:
            continue
        summary = moves.entry_summary(e)
        if not keep(title, summary):
            continue
        items.append({
            "source": name,
            "title": title,
            "url": link,
            "date": d.date().isoformat() if d else "",
            "summary": summary[:600],
        })
    return items


def title_key(title: str) -> str:
    key = re.sub(r"\s+-\s+[a-z][\w .&'-]{2,40}$", "", moves.norm(title).lower())
    return re.sub(r"\W+", " ", key).strip()


def gather(days: int, use_google: bool) -> list[dict]:
    """Every candidate story, each tagged with the section whose sources found it (`hint`)."""
    since = datetime.now(timezone.utc) - timedelta(days=days)
    items: list[dict] = []

    def add(hint: str, got: list[dict]) -> None:
        for it in got:
            it["hint"] = hint
        items.extend(got)

    print("Hires and exits...", file=sys.stderr)
    for name, url, music_filter in moves.FEEDS:
        add("moves", moves.fetch_feed(name, url, since, music_filter))
    if use_google:
        for q in moves.GOOGLE_NEWS_QUERIES:
            add("moves", moves.fetch_feed(f"Google News: {q[:50]}", moves.google_news_url(q, days), since, True))

    print("Concerts...", file=sys.stderr)
    for name, url, _ in CONCERT_FEEDS:
        add("concerts", fetch_plain(name, url, since, looks_like_concert))
    if use_google:
        for q in CONCERT_QUERIES:
            add("concerts", fetch_plain(f"Google News: {q[:50]}", moves.google_news_url(q, days), since, looks_like_concert))

    print("Ticketing...", file=sys.stderr)
    for name, url, _ in TICKETING_FEEDS:
        add("ticketing", fetch_plain(name, url, since, looks_like_ticketing))
    if use_google:
        for q in TICKETING_QUERIES:
            add("ticketing", fetch_plain(f"Google News: {q[:50]}", moves.google_news_url(q, days), since, looks_like_ticketing))

    # one row per story: the same URL (or the same headline from Google News) only once
    uniq: dict[str, dict] = {}
    for it in items:
        for key in (it["url"], title_key(it["title"])):
            if key in uniq:
                break
        else:
            uniq[it["url"]] = it
            uniq[title_key(it["title"])] = it
    seen_ids: set[int] = set()
    result = []
    for it in uniq.values():
        if id(it) not in seen_ids:
            seen_ids.add(id(it))
            result.append(it)
    print(f"{len(result)} candidate stories", file=sys.stderr)
    return result


# --------------------------------------------------------------------------------------
# Claude: section, event date, one-line summary, duplicate key
# --------------------------------------------------------------------------------------

LLM_SCHEMA = {
    "type": "object",
    "properties": {
        "stories": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "item_id": {"type": "integer"},
                    "section": {"type": "string", "enum": ["moves", "concerts", "ticketing", "skip"]},
                    "event_date": {"type": "string", "description": "YYYY-MM-DD, or empty when it cannot be worked out"},
                    "summary": {"type": "string", "description": "one sentence, under 30 words, past tense, names included"},
                    "story_key": {"type": "string", "description": "short lowercase slug shared by every item about the same development"},
                },
                "required": ["item_id", "section", "event_date", "summary", "story_key"],
                "additionalProperties": False,
            },
        }
    },
    "required": ["stories"],
    "additionalProperties": False,
}

LLM_PROMPT = """You are preparing a short brief for the founder of a concert-ticketing startup. Today is {today}.

For each numbered item (outlet, article date, headline, snippet) decide:

section
- moves: a named person hired, promoted, departing, fired or retiring at a music-industry company (label, publisher,
  promoter, agency, venue operator, streaming service, ticketing company, trade body), or layoffs at one.
- concerts: tours, residencies, festival lineups, shows announced, cancelled, postponed or played, ticket on-sale
  dates, box-office records, venue openings. Music only.
- ticketing: the ticketing industry: Ticketmaster, Live Nation, StubHub, SeatGeek, AXS, DICE, Eventbrite and the
  like; resale and scalping; fees and pricing rules; antitrust cases, regulation and legislation about tickets;
  ticketing deals, launches, funding, outages, fraud.
- skip: reviews, interviews, lists, album and song releases, charts, awards, sports, film and TV, stock-tip pieces,
  anything not about the music business, and anything whose article date and content make clear the development is
  more than {days} days old.

event_date
- The date the development itself happened or was announced: the appointment or exit announcement, the day the
  tour or lineup was announced, the day a show was cancelled, the day a ruling or deal was made. NOT the article's
  date when the article is clearly reporting something that happened earlier (a recap, an anniversary, a profile).
- When the text says "today", "this week", "on Tuesday" or gives no date, assume it happened on the article date and
  resolve weekdays against it. Use YYYY-MM-DD. Leave it empty only when you truly cannot tell.
- A future tour or show is news on the day it was announced: use the announcement date, not the show date.

summary: one sentence, under 30 words, with the names, the company and the role or the concrete fact. Plain text.

story_key: a short lowercase slug for the development (e.g. "rush-sphere-residency", "ticketmaster-ftc-bots-ruling",
"jane-doe-named-president-atlantic"). Items about the same development must share the same key so they can be merged.

Items:
"""


def llm_classify(items: list[dict], model: str, today: date, days: int) -> dict[int, dict]:
    import anthropic

    client = anthropic.Anthropic()
    results: dict[int, dict] = {}
    batch = 30
    for start in range(0, len(items), batch):
        chunk = items[start:start + batch]
        lines = []
        for i, it in enumerate(chunk):
            src = it["source"].split(":")[0]
            lines.append(
                f"{start + i}. [{src}] article date: {it.get('date') or 'unknown'}\n"
                f"   {moves.norm(it['title'])}\n   {moves.norm(it.get('summary', ''))[:500]}"
            )
        prompt = LLM_PROMPT.format(today=today.isoformat(), days=days) + "\n".join(lines)
        resp = client.beta.messages.create(
            model=model,
            max_tokens=16000,
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
            output_config={"format": {"type": "json_schema", "schema": LLM_SCHEMA}},
            messages=[{"role": "user", "content": prompt}],
        )
        if resp.stop_reason == "refusal":
            print("  ! Claude declined a batch; those items are dropped", file=sys.stderr)
            continue
        text = next(b.text for b in resp.content if b.type == "text")
        for st in json.loads(text)["stories"]:
            results[st["item_id"]] = st
        print(f"  classified {min(start + batch, len(items))}/{len(items)}", file=sys.stderr)
    return results


def rule_classify(items: list[dict]) -> dict[int, dict]:
    """Without Claude: the section whose sources found the story, and the publish date as the event date."""
    out = {}
    for i, it in enumerate(items):
        out[i] = {
            "item_id": i,
            "section": it["hint"],
            "event_date": it.get("date", ""),
            "summary": moves.norm(it["title"]),
            "story_key": title_key(it["title"]),
        }
    return out


# --------------------------------------------------------------------------------------
# Selection and rendering
# --------------------------------------------------------------------------------------

def parse_day(s: str) -> date | None:
    try:
        return date.fromisoformat(s.strip()[:10]) if s and s.strip() else None
    except ValueError:
        return None


def select(items: list[dict], classified: dict[int, dict], today: date, days: int, seen_keys: set[str]) -> dict[str, list[dict]]:
    """Group kept stories by section; one entry per story_key, newest first."""
    low = today - timedelta(days=days)
    merged: dict[tuple[str, str], dict] = {}
    for idx, it in enumerate(items):
        st = classified.get(idx)
        if not st or st["section"] == "skip":
            continue
        d = parse_day(st.get("event_date", ""))
        if d is None or d < low or d > today:
            continue
        key = (st["section"], st["story_key"] or title_key(it["title"]))
        entry = merged.get(key)
        source = it["source"].split(":")[0]
        if entry is None:
            merged[key] = {
                "section": st["section"], "key": key[1], "date": d, "summary": st["summary"].strip() or moves.norm(it["title"]),
                "headline": moves.norm(it["title"]), "url": it["url"], "source": source, "also": [],
            }
        else:
            if source not in (entry["source"], *entry["also"]):
                entry["also"].append(source)
            # prefer a real article URL to a Google News redirect
            if "news.google.com" in entry["url"] and "news.google.com" not in it["url"]:
                entry["url"], entry["source"], entry["headline"] = it["url"], source, moves.norm(it["title"])
    grouped: dict[str, list[dict]] = {s: [] for s, _ in SECTIONS}
    for e in merged.values():
        if e["key"] in seen_keys:
            continue
        grouped[e["section"]].append(e)
    for lst in grouped.values():
        lst.sort(key=lambda e: (e["date"], e["summary"]), reverse=True)
    return grouped


def render(grouped: dict[str, list[dict]], today: date, days: int, note: str | None) -> tuple[str, str]:
    total = sum(len(v) for v in grouped.values())
    when = today.strftime("%a %d %b %Y")
    subject = f"Industry brief, {when}: " + (f"{total} item{'s' if total != 1 else ''}" if total else "no updates")
    lines = [f"Industry brief for {when}. Developments from the last {days} days (dated by the event, not the article).", ""]
    if note:
        lines += [f"Note: {note}", ""]
    if not total:
        lines.append("No updates.")
    for section, title in SECTIONS:
        entries = grouped.get(section) or []
        lines.append(f"{title} ({len(entries)})")
        lines.append("-" * len(f"{title} ({len(entries)})"))
        if not entries:
            lines.append("No updates.")
        for e in entries:
            also = f"; also {', '.join(e['also'][:3])}" if e["also"] else ""
            lines.append(f"* {e['date'].strftime('%b %d')}: {e['summary']}")
            lines.append(f"  {e['source']}{also}: {e['url']}")
        lines.append("")
    lines.append("Sent by crawlers/industry_newsletter.py (firstline repo).")
    return subject, "\n".join(lines) + "\n"


def send(subject: str, body: str, to: str) -> str:
    host = os.environ.get("NEWSLETTER_SMTP_HOST", "smtp.gmail.com")
    port = int(os.environ.get("NEWSLETTER_SMTP_PORT", "587"))
    user = os.environ.get("NEWSLETTER_SMTP_USER", "")
    password = os.environ.get("NEWSLETTER_SMTP_PASSWORD", "")
    sender = os.environ.get("NEWSLETTER_FROM") or user
    if not user or not password:
        raise SystemExit("NEWSLETTER_SMTP_USER and NEWSLETTER_SMTP_PASSWORD are not set (see --help); use --dry-run to print instead")
    msg = EmailMessage()
    msg["Subject"] = subject
    msg["From"] = sender
    msg["To"] = to
    msg["Date"] = formatdate(localtime=True)
    msg["Message-ID"] = make_msgid()
    msg.set_content(body)
    with smtplib.SMTP(host, port, timeout=60) as smtp:
        smtp.ehlo()
        smtp.starttls()
        smtp.login(user, password)
        smtp.send_message(msg)
    return msg["Message-ID"]


# --------------------------------------------------------------------------------------
# State: story keys already emailed (so the second email of the week has only new items)
# --------------------------------------------------------------------------------------

def load_state(path: str | None) -> dict[str, str]:
    if not path or not os.path.exists(path):
        return {}
    with open(path) as f:
        data = json.load(f)
    return dict(data.get("sent", {}))


def save_state(path: str | None, sent: dict[str, str], today: date) -> None:
    if not path:
        return
    cutoff = (today - timedelta(days=45)).isoformat()
    kept = {k: v for k, v in sent.items() if v >= cutoff}
    with open(path, "w") as f:
        json.dump({"sent": dict(sorted(kept.items())), "updated": datetime.now(timezone.utc).isoformat()}, f, indent=0)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--days", type=int, default=WINDOW_DAYS, help=f"event window in days (default {WINDOW_DAYS})")
    ap.add_argument("--to", default=os.environ.get("NEWSLETTER_TO", DEFAULT_TO), help="recipient")
    ap.add_argument("--dry-run", action="store_true", help="print the email; send nothing, update no state")
    ap.add_argument("--out", help="also write the email text here")
    ap.add_argument("--state", help="JSON file of story keys already emailed; they are not repeated")
    ap.add_argument("--no-google", action="store_true", help="skip Google News searches")
    ap.add_argument("--no-llm", action="store_true", help="skip Claude: keyword sections and publish dates (testing only)")
    ap.add_argument("--model", default=DEFAULT_MODEL, help=f"model for the Claude step (default {DEFAULT_MODEL})")
    args = ap.parse_args()

    today = datetime.now().date()
    note = None
    try:
        items = gather(args.days + 2, not args.no_google)  # fetch a little wider; the event date decides
        if args.no_llm:
            classified = rule_classify(items)
            note = "Claude was not used: sections are by source and dates are publish dates, not event dates."
        else:
            classified = llm_classify(items, args.model, today, args.days)
        sent = load_state(None if args.dry_run else args.state)
        grouped = select(items, classified, today, args.days, set(sent))
        subject, body = render(grouped, today, args.days, note)
    except Exception:  # noqa: BLE001 - the email is the only thing the owner sees; say what broke
        err = traceback.format_exc()
        print(err, file=sys.stderr)
        subject = f"Industry brief, {today.strftime('%a %d %b %Y')}: run failed"
        body = f"The newsletter run failed; nothing below was checked. Error:\n\n{err}\n"
        grouped, sent = {}, {}
        if args.dry_run:
            print(f"Subject: {subject}\n\n{body}")
            return 1

    if args.out:
        with open(args.out, "w") as f:
            f.write(f"Subject: {subject}\n\n{body}")
    if args.dry_run:
        print(f"Subject: {subject}\n\n{body}")
        return 0

    message_id = send(subject, body, args.to)
    print(f"Sent {message_id} to {args.to}: {subject}", file=sys.stderr)
    if grouped:
        for entries in grouped.values():
            for e in entries:
                sent[e["key"]] = today.isoformat()
        save_state(args.state, sent, today)
    return 0 if "run failed" not in subject else 1


if __name__ == "__main__":
    sys.exit(main())
