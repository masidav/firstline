#!/usr/bin/env python3
"""Find hires, promotions, departures and layoffs at music companies.

Pulls the trade press that reports executive moves (Billboard's Executive Turntable,
Music Business Worldwide, Variety, The Hollywood Reporter, Pollstar, MusicRow, CMU,
Music Ally, Digital Music News) plus Google News searches that also surface paywalled
or feed-less outlets (Puck, Hits Daily Double, Music Week, Record of the Day).

For every matching story it extracts: person, company, position, action
(hired / promoted / departed / laid_off / other) and prints a table, CSV or JSON.

    python3 music_industry_moves.py                 # last 7 days, table on stdout
    python3 music_industry_moves.py --days 14 --format csv --out moves.csv
    python3 music_industry_moves.py --state seen.json   # only show stories not seen before
    python3 music_industry_moves.py --llm           # Claude does the extraction (ANTHROPIC_API_KEY)

Headline parsing is rule-based and tuned to how these outlets write headlines. It is
right most of the time; the headline and URL are always kept so anything odd can be
checked by hand. `--llm` sends headline + first paragraph to Claude for extraction,
which handles roundup items ("Interscope Promotes Three") and odd phrasings better.
"""

from __future__ import annotations

import argparse
import csv
import html
import json
import os
import re
import sys
import time
from dataclasses import dataclass, asdict, field
from datetime import datetime, timedelta, timezone
from email.utils import parsedate_to_datetime
from urllib.parse import quote_plus

import feedparser
import requests
from bs4 import BeautifulSoup

HEADERS = {
    "User-Agent": "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
    # brotli is not always decodable in every Python build; gzip is universal
    "Accept-Encoding": "gzip, deflate",
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
}
TIMEOUT = 30

# --------------------------------------------------------------------------------------
# Sources
# --------------------------------------------------------------------------------------

# music_filter=True means the feed is broad (or a general news search) and each item
# must also mention something music-industry-ish before it counts.
FEEDS = [
    # name, url, music_filter
    ("Billboard Executive Turntable", "https://www.billboard.com/t/executive-turntable/feed/", False),
    ("Billboard Layoffs", "https://www.billboard.com/t/layoffs/feed/", False),
    ("Music Business Worldwide", "https://www.musicbusinessworldwide.com/feed/", False),
    ("Variety Music", "https://variety.com/v/music/feed/", False),
    ("Hollywood Reporter Music", "https://www.hollywoodreporter.com/c/music/feed/", False),
    ("Pollstar", "https://news.pollstar.com/feed/", False),
    ("MusicRow", "https://musicrow.com/feed/", False),
    ("Complete Music Update", "https://completemusicupdate.com/feed/", False),
    ("Music Ally", "https://musically.com/feed/", False),
    ("Digital Music News", "https://www.digitalmusicnews.com/feed/", False),
]

# Google News searches. `when:Nd` is filled in from --days. These catch outlets without
# usable feeds (Hits Daily Double, Music Week, Record of the Day) and paywalled ones
# whose headlines are still indexed (Puck).
GOOGLE_NEWS_QUERIES = [
    '"record label" (appoints OR names OR hires OR promotes OR exits OR "steps down" OR layoffs)',
    '(music OR label OR records) exec (named OR appointed OR promoted OR hired OR exits OR "steps down" OR ousted OR "laid off")',
    '("Universal Music" OR "Sony Music" OR "Warner Music" OR Interscope OR "Republic Records" OR "Atlantic Records" OR "Columbia Records" OR "Capitol Records" OR "Def Jam" OR "RCA Records") (appoints OR names OR promotes OR hires OR exits OR layoffs OR "steps down")',
    '("music publishing" OR "Music Group" OR "Music Nashville" OR "Music Latin" OR "Music UK") (appoints OR names OR promotes OR hires OR "steps down" OR exits)',
    '("Live Nation" OR AEG OR Wasserman OR WME OR CAA OR UTA OR "Oak View Group" OR Ticketmaster OR Spotify OR "Apple Music" OR "Amazon Music" OR YouTube Music OR TikTok OR SoundCloud OR "Downtown Music" OR Believe OR Kobalt OR BMG OR Concord OR Reservoir OR Primary Wave OR Hipgnosis) music (names OR appoints OR promotes OR hires OR exits OR "steps down" OR layoffs)',
    '("head of promotion" OR "VP of promotion" OR "promotion" OR "radio promotion" OR "A&R" OR "marketing") (label OR records OR "music group") (named OR appoints OR promotes OR hires OR joins OR exits)',
    'site:puck.news music (label OR exec OR Universal OR Sony OR Warner OR Spotify OR Live Nation)',
    'site:hitsdailydouble.com (named OR appointed OR promoted OR hires OR exits OR upped OR "steps down" OR "out at")',
    'site:musicweek.com (appoints OR names OR promotes OR hires OR joins OR exits OR "steps down")',
    'site:recordoftheday.com (appoints OR names OR promotes OR joins OR exits)',
    'site:variety.com music (appoints OR names OR promotes OR hires OR exits OR "steps down" OR layoffs OR ousted)',
    'site:hollywoodreporter.com music (appoints OR names OR promotes OR hires OR exits OR "steps down" OR layoffs OR ousted)',
    'site:billboard.com (appoints OR names OR promotes OR hires OR exits OR "steps down" OR layoffs OR ousted OR retires)',
    'site:musicbusinessworldwide.com (appoints OR names OR promotes OR hires OR joins OR exits OR "steps down" OR layoffs)',
]


def google_news_url(query: str, days: int) -> str:
    q = f"{query} when:{days}d"
    return f"https://news.google.com/rss/search?q={quote_plus(q)}&hl=en-US&gl=US&ceid=US:en"


# --------------------------------------------------------------------------------------
# Filters
# --------------------------------------------------------------------------------------

MOVE_WORDS = re.compile(
    r"\b("
    r"appoint(s|ed|ment)?|name[sd]?|tap(s|ped)|hire[sd]?|hiring|hirings|join(s|ed)|promot(es|ed|ion|ions)|"
    r"elevat(es|ed)|up(s|ped) to|upped|welcomes|brings on|"
    r"exit(s|ed)?|depart(s|ed|ure|ing)|leav(es|ing)|steps? down|stepping down|step(s|ped) back|"
    r"resign(s|ed|ation)|retir(es|ed|ing|ement)|out at|ousted|fired|let go|laid off|lay(s|ing)? off|layoffs?|"
    r"job cuts|cuts (staff|jobs)|trims staff|slashes|restructur(e|es|ing)|shake-?up|"
    r"new (ceo|president|chairman|chair|chief|head|gm|general manager|svp|evp|vp)|"
    r"takes over|to lead|to head|to run|to oversee|named to|poached|lands at|moves to|"
    r"expands (role|duties)|new role|executive turntable|turntable"
    r")\b",
    re.I,
)

# Matches MOVE_WORDS but is not a people move.
NOT_MOVE = re.compile(
    r"\b(playlist|chart|tour dates?|setlist|lineup|line-up|nominees?|honorees?|recipients?|"
    r"grammys?|award show|box office|new music friday|review|premiere|music video|"
    r"announces (tour|album|single|dates)|partnership|partners with|q&a|op-ed|"
    r"names? (street|stage|room|building|venue|award|prize|song|album|track|single)|in the name of|"
    r"logo|brand identity|adds support|support for|stems|inks (with|deal)|signs (with|to)|record deal|"
    r"to watch|power list|power players|hot list|rising stars|40 under 40|30 under 30|named among|named in|named one of)\b",
    re.I,
)
# Extra exclusions for broad sources (general news searches), where film/TV stories about
# the same conglomerates slip through the music-word check.
NOT_MOVE_BROAD = re.compile(
    r"\b(film|films|movie|movies|tv series|television|pictures|studios?|netflix|streaming giant|"
    r"stock|shares|analyst|price target|earnings|untitled)\b",
    re.I,
)

MUSIC_WORDS = re.compile(
    r"\b(music|label|records|recordings|publishing|songwriter|a&r|artist|streaming|spotify|apple music|"
    r"amazon music|youtube music|tidal|deezer|soundcloud|tiktok|universal|umg|sony|warner|wmg|interscope|"
    r"republic|atlantic|columbia|capitol|def jam|rca|epic|island|geffen|motown|arista|elektra|"
    r"concord|bmg|kobalt|downtown|believe|reservoir|primary wave|hipgnosis|peermusic|ascap|bmi|sesac|"
    r"riaa|nmpa|recording academy|live nation|ticketmaster|aeg|wasserman|wme|caa|uta|oak view|"
    r"pollstar|billboard|promoter|touring|concert|venue|arena|radio|iheart|audacy|cumulus|sirius|"
    r"mca|hybe|yg|sm entertainment|jyp|empire|300|quality control|roc nation|"
    r"nashville|latin|k-pop|hip-hop|country music|merlin|ingrooves|the orchard|awal|distrokid|tunecore|"
    r"vevo|shazam|sync|synch|licensing|catalog|royalt)\w*",
    re.I,
)


def looks_like_move(title: str, summary: str, broad: bool = False) -> bool:
    text = f"{title} {summary[:300]}"
    if NOT_MOVE.search(title):
        return False
    if broad and NOT_MOVE_BROAD.search(title):
        return False
    return bool(MOVE_WORDS.search(text))


def looks_like_music(title: str, summary: str) -> bool:
    return bool(MUSIC_WORDS.search(f"{title} {summary[:400]}"))


# --------------------------------------------------------------------------------------
# Headline parsing
# --------------------------------------------------------------------------------------

TITLES = (
    r"(?:global |worldwide |executive |senior |associate |assistant |deputy |interim |co-|vice |general |"
    r"managing |chief |creative |international |regional |national |head |group |corporate |new |next |first )*"
    r"(?:ceo|coo|cfo|cmo|cto|cro|cco|cpo|chro|president|chairman|chairwoman|chair|chief [a-z]+ officer|"
    r"vice chairman|vice chair|head|director|gm|general manager|svp|evp|vp|managing director|"
    r"partner|publisher|editor|a&r|manager|lead|executive|exec|agent|counsel|officer|founder|co-presidents?|"
    r"presidents?|chiefs?|heads?|directors?)"
)
TITLE_RE = re.compile(rf"\b(?:{TITLES})\b", re.I)
TITLE_WORD = re.compile(
    r"^(?:ceo|coo|cfo|cmo|cto|cro|cco|cpo|chro|president|presidents|chairman|chairwoman|chair|chief|vice|"
    r"head|heads|director|directors|gm|manager|svp|evp|vp|partner|publisher|editor|exec|executive|agent|"
    r"counsel|officer|founder|general|senior|global|worldwide|associate|assistant|deputy|interim|managing|"
    r"creative|international|regional|national|group|corporate|co-president|co-presidents|chiefs)$",
    re.I,
)

# Words that never belong to a person's name.
NAME_STOP = {
    "of", "the", "and", "as", "to", "for", "in", "at", "on", "by", "with", "from", "new", "next",
    "first", "its", "his", "her", "their", "former", "ex", "exec", "veteran", "longtime", "music", "records",
    "record", "recordings", "group", "label", "labels", "publishing", "division", "team", "entertainment",
    "media", "digital", "marketing", "promotion", "promotions", "sales", "creative", "policy", "affairs",
    "communications", "comms", "business", "operations", "finance", "legal", "strategy", "content",
    "programming", "sync", "synch", "touring", "talent", "brand", "data", "technology", "product",
    "americas", "america", "europe", "asia", "latin", "global", "international", "uk", "us", "north",
    "south", "east", "west", "pacific", "northwest", "northeast", "southeast", "southwest", "coast",
    "award", "awards", "festival", "foundation", "coalition", "board", "committee", "staff", "jobs",
    "layoffs", "nashville", "london", "york", "angeles", "miami", "atlanta", "chicago", "canada", "australia",
    "cbe", "obe", "mbe", "phd", "inc", "llc", "ltd", "co", "street", "amid", "after", "following", "named",
    "appointed", "promoted", "hired", "joins", "exits", "leaves", "departs", "retires", "announces",
    "unveils", "launches", "goes", "all-in", "ai", "three", "two", "four", "five", "six", "several",
    "expansion", "hires", "hire", "human", "resources", "support", "stems", "identity", "logo", "radio",
    "television", "film", "pictures", "studios", "universal", "sony", "warner", "spotify", "apple",
    "amazon", "youtube", "google", "tiktok", "netflix", "disney", "ticketmaster", "billboard", "variety",
    "reorganization", "restructure", "restructuring", "shake-up", "sync", "post", "posts", "role", "roles",
}
PARTICLES = {"de", "van", "von", "da", "del", "della", "di", "la", "le", "du", "bin", "al", "el", "der", "den"}
SUFFIXES = {"jr", "jr.", "sr", "sr.", "ii", "iii", "iv"}

TOKEN = re.compile(r"[A-Za-z][\w'’-]*\.?|\"[^\"]+\"|“[^”]+”")


def _is_name_token(tok: str, first: bool = False) -> bool:
    t = tok.strip(",")
    if not t:
        return False
    low = t.lower().rstrip(".")
    if low in PARTICLES:
        return not first
    if low in SUFFIXES:
        return not first
    if t.startswith(('"', "“")):
        return not first
    if low in NAME_STOP or TITLE_WORD.match(low):
        return False
    if not t[0].isupper():
        return False
    if re.fullmatch(r"[A-Z]\.?", t):  # initial
        return True
    if t.isupper() and len(t) > 3 and "." not in t:  # acronyms like UMPG, HYBE
        return False
    if re.fullmatch(r"(?:[A-Z]\.)+", t):  # "J.J."
        return True
    if t.endswith(".") and len(t) > 2:  # "Briegmann." - sentence end, not an initial
        return False
    return True


def first_name_in(text: str) -> tuple[str, str]:
    """Return (name, remainder) with the name taken from the start of text:
    'Chris Hammond Senior Vice President Of ...' -> ('Chris Hammond', 'Senior Vice President Of ...')."""
    toks = text.split()
    if len(toks) < 2 or not _is_name_token(toks[0], first=True):
        return "", text
    n = 1
    while n < len(toks) and n < 5 and _is_name_token(toks[n]):
        n += 1
        tok = toks[n - 1].lower().rstrip(".,")
        # a plain capitalized third token is kept only if it's a particle/initial/suffix/nickname
        # or if nothing name-like follows (3-token names like "Juan Carlos Perez")
        if n >= 2 and n < len(toks) and not (
            tok in PARTICLES or tok in SUFFIXES or re.fullmatch(r"[a-z]", tok) or toks[n - 1].startswith(('"', "“"))
        ):
            nxt = toks[n].strip(",")
            if _is_name_token(nxt) and not (nxt.lower() in PARTICLES or nxt.lower().rstrip(".") in SUFFIXES or re.fullmatch(r"[A-Z]\.?", nxt)):
                break
        if toks[n - 1].endswith(","):
            break
    if n < 2:
        return "", text
    name = " ".join(toks[:n]).strip(",")
    rest = " ".join(toks[n:])
    if toks[n - 1].endswith(","):
        rest = ", " + rest
    return name.strip(), rest.strip()


def last_name_in(text: str) -> tuple[str, str]:
    """Return (name, text_before_name) for the last person name in text:
    'ex-Big Deal Music and RCA exec Pete Robinson' -> ('Pete Robinson', 'ex-Big Deal Music and RCA exec')."""
    toks = text.split()
    for i in range(0, len(toks) - 1):
        name, rest = first_name_in(" ".join(toks[i:]))
        if name and not rest:
            return name, " ".join(toks[:i]).strip(" ,")
    return "", text


HONORIFIC = re.compile(r"^(?:CBE|OBE|MBE|DBE|KBE|PhD|Esq\.?|KC|QC|MP)\s+")


def leading_names(text: str) -> tuple[list[str], str]:
    """'Clio Massey, Matt D'Arduini Tapped as ...' -> (['Clio Massey', "Matt D'Arduini"], 'Tapped as ...')."""
    names = []
    rest = text
    while True:
        name, after = first_name_in(rest)
        if not name:
            break
        names.append(name)
        after = HONORIFIC.sub("", after)
        m = re.match(r"^(?:,\s*|and\s+|&\s+)(?=[A-Z])", after)
        if m and len(names) < 6:
            rest = after[m.end():]
            continue
        rest = after
        break
    return names, rest


def split_names(text: str) -> list[str]:
    """'Julio Bagué and Lalo Molina' / 'Clio Massey, Matt D'Arduini' -> both names."""
    names = []
    for piece in re.split(r",\s*|\s+and\s+|\s+&\s+", text):
        n, _ = last_name_in(piece.strip())
        if n:
            names.append(n)
    return names


COMPANY_HINT = re.compile(
    r"\b(records|recordings|music|group|publishing|entertainment|media|agency|inc|llc|ltd|corp|company|"
    r"foundation|organisation|organization|association|coalition|presents|live|nation|studios|label|"
    r"distribution|management|network|networks|radio|academy|society|rights|collective|ventures|partners|"
    r"capital|holdings|international|worldwide|limited|plc|gmbh|sa|ag|bv|kk|bmi|ascap|sesac|riaa|nmpa|"
    r"spotify|kobalt|believe|hybe|umg|wmg|smpe|umpg|sony|warner|universal|interscope|republic|atlantic|"
    r"columbia|capitol|rca|epic|island|geffen|motown|arista|elektra|concord|bmg|downtown|reservoir|"
    r"hipgnosis|peermusic|ticketmaster|aeg|wasserman|wme|caa|uta|empire|tiktok|youtube|apple|amazon|"
    r"deezer|tidal|soundcloud|pandora|sirius|iheart|audacy|cumulus|imro|prs|ppl|bpi|aim|ifpi|cma|acm)\b",
    re.I,
)
DEPARTMENT = re.compile(
    r"^(?:a&r|marketing|promotion|promotions|sales|digital|creative|policy|public affairs|communications|"
    r"comms|business|business affairs|operations|finance|legal|strategy|content|programming|sync|synch|"
    r"touring|talent|partnerships|brand|data|technology|product|engineering|hr|people|research|"
    r"americas|america|europe|asia|latin|global|international|north america|uk|nashville|streaming|"
    r"artist|audience|growth|revenue|commercial|catalog|catalogue|licensing|film|tv|games|"
    r"the americas|the uk|emea|apac|latam|aunz|canada|australia|mexico|brazil|spain|france|germany|italy|"
    r"japan|korea|india|africa|the company|the label|the group)\b",
    re.I,
)


def looks_like_company(s: str) -> bool:
    s = s.strip(" ,.")
    if not s or DEPARTMENT.match(s):
        return False
    if COMPANY_HINT.search(s):
        return True
    toks = s.split()
    return len(toks) <= 2 and all(t[0].isupper() for t in toks)


def norm(s: str) -> str:
    s = html.unescape(s)
    s = s.replace("’", "'").replace("‘", "'").replace("“", '"').replace("”", '"').replace("–", "-").replace("—", "-")
    s = re.sub(r"\s+", " ", s).strip()
    s = re.sub(r"^(?:executive turntable|breaking|exclusive|update|report|confirmed|official)\s*:\s*", "", s, flags=re.I)
    return s


CUT_COMPANY = re.compile(
    r"\s+(?:after|amid|following|as part of|in a|in an|ahead of|to lead|to oversee|to head|to run|with|where|who|"
    r"effective|starting|beginning|unveils|announces|goes|restructures|cuts|launches|reveals|confirms|as it|"
    r"to launch|to start|to form|to join|in|for)\b|[:;]|\s+-\s+|\s+'|\s+\"",
    re.I,
)
CUT_POSITION = re.compile(
    r"\s+(?:after|amid|following|effective|starting|beginning|replacing|succeeding|reporting to|as it|as the|"
    r"as part of|in a|in an|overseeing|to lead|to oversee|to head|to run|to drive|to build|to launch|to start|"
    r"to spearhead|to expand|to grow|to steer|leading|where)\b|[:;]|\s+-\s+",
    re.I,
)


def clean_company(s: str) -> str:
    s = s.strip(" ,.;:-")
    s = CUT_COMPANY.split(s, maxsplit=1)[0]
    s = s.split(", ", 1)[0]
    s = re.sub(r"^(?:the|its|his|her|their|at|from|of|for)\s+", "", s, flags=re.I)
    s = re.sub(r"'s$", "", s)
    s = s.strip(" ,.;:-")
    if s.lower() in {"company", "label", "group", "firm", "agency", "organization", "organisation"}:
        return ""
    return s


def clean_position(s: str) -> str:
    s = s.strip(" ,.;:-")
    s = re.sub(r"^(?:as|to|its|new|next|the|a|an|to be|to become|to the (?:role|post|position) of|position of|role of|its new|its next|the new|the next|the role of|the post of)\s+", "", s, flags=re.I)
    s = re.sub(r"^(?:as|to|its|new|the|a|an)\s+", "", s, flags=re.I)
    s = CUT_POSITION.split(s, maxsplit=1)[0]
    return s.strip(" ,.;:-")


def split_position_company(rest: str) -> tuple[str, str]:
    """'VP of Promotion at Curb Records' -> ('VP of Promotion', 'Curb Records');
    'President of Global Label Services Division' -> (same, '');
    'CEO of Kobalt' -> ('CEO', 'Kobalt')."""
    rest = re.sub(r"^(?:as|to|as the|as its|as the new|as new|to the|to be)\s+", "", rest.strip(" ,."), flags=re.I)
    for sep in (" at ", " with ", " for "):
        if sep in rest:
            pos, comp = rest.rsplit(sep, 1)
            if TITLE_RE.search(pos) or not pos:
                return clean_position(pos), clean_company(comp)
    # "... of X" counts as the company only when X looks like one, not a department
    parts = re.split(r"\s+of\s+", rest, flags=re.I)
    for i in range(1, len(parts)):
        pos, comp = " of ".join(parts[:i]), " of ".join(parts[i:])
        if TITLE_RE.search(pos) and looks_like_company(comp) and not DEPARTMENT.match(comp):
            return clean_position(pos), clean_company(comp)
    # "Arista Records Co-Presidents" -> company then title
    pm = re.match(rf"^(?P<comp>[A-Z][\w&'.-]*(?:\s+[A-Z][\w&'.-]*)*)\s+(?P<pos>(?i:{TITLES})\b.*)$", rest)
    if (
        pm
        and looks_like_company(pm.group("comp"))
        and not any(TITLE_WORD.match(w) for w in pm.group("comp").split())
        and pm.group("comp").split()[0].lower() not in {"new", "its", "next", "the", "board"}
    ):
        return clean_position(pm.group("pos")), clean_company(pm.group("comp"))
    return clean_position(rest), ""


@dataclass
class Move:
    date: str
    action: str
    name: str
    position: str
    company: str
    headline: str
    source: str
    url: str
    confidence: str = "rule"
    extra: dict = field(default_factory=dict)


HIRE_VERBS = (
    r"(?:names|name|appoints|appoint|taps|tap|hires|hire|recruits|recruit|welcomes|brings on|brings in|brings back|"
    r"poaches|installs|picks|selects|chooses|"
    r"(?:has |have |had )?(?:named|appointed|tapped|hired|recruited|welcomed|installed|picked|selected|chosen)|"
    r"announces (?:the )?(?:appointment|hiring|hire|addition|arrival) of|adds)"
)
PROMO_VERBS = (
    r"(?:promotes|promote|elevates|elevate|ups|upped|bumps|expands (?:the )?(?:role|duties|remit) of|"
    r"(?:has |have |had )?(?:promoted|elevated|bumped)|announces (?:the )?(?:promotion|promotions) of)"
)
LEAVE_VERBS_PERSON = (
    r"(?:exits|exit|leaves|leave|departs|depart|steps down|stepping down|steps back|resigns|resign|retires|retire|"
    r"retiring|is out|out|ousted|fired|let go|laid off|leaving|departing|exiting|to exit|to leave|to depart|"
    r"to step down|to retire|quits|has left|has exited|has departed|has resigned|has retired|has stepped down|"
    r"will step down|will exit|will leave|will depart|will retire|set to exit|set to leave|set to depart)"
)
LEAVE_VERBS_COMPANY = r"(?:ousts|fires|lets go|parts ways with|drops|removes|replaces|loses|cuts ties with|has fired|has ousted|has parted ways with)"
LAYOFF = r"(?:lays off|laying off|to lay off|cuts|cutting|to cut|trims|slashes|eliminates|layoffs?|job cuts|restructur\w+|shake-?up|reduces (?:staff|headcount|workforce)|axes)"
PASSIVE_HIRE = r"(?:named|appointed|tapped|hired|chosen|selected|set|installed|confirmed|unveiled|revealed|announced)"
PASSIVE_PROMO = r"(?:promoted|elevated|upped|bumped|moved up)"
STRONG_HIRE = re.compile(r"^(?:appoints?|appointed|taps?|tapped|hires?|hired|recruits?|recruited|promotes?|promoted|elevates?|elevated|ups|upped|names?|named|announces)", re.I)

CLAUSE_SPLIT = re.compile(
    rf"(?:,\s+(?:and\s+)?|\s+and\s+)(?=(?:{HIRE_VERBS}|{PROMO_VERBS}|{LEAVE_VERBS_COMPANY})\s+[A-Z])",
    re.I,
)
SECOND_SUBJECT = re.compile(
    rf"^(?P<left>.+?),\s+(?P<right>(?:[A-Z][\w&'.-]*\s+){{1,8}}(?i:{LEAVE_VERBS_PERSON}|{PASSIVE_HIRE}|{PASSIVE_PROMO}|joins|launches)\b.*)$"
)


def _mk(action, name="", position="", company=""):
    return {
        "action": action,
        "name": name.strip(" ,.'\""),
        "position": clean_position(position),
        "company": clean_company(company),
    }


def parse_headline(title: str) -> list[dict]:
    """Rule-based extraction. Returns a list because roundup headlines contain several moves."""
    t = norm(title)
    # Google News appends " - Outlet"
    t = re.sub(r"\s+-\s+[A-Z][\w .&'-]{2,40}$", "", t)
    t = re.sub(r"\s+-\s*$", "", t)
    out = []
    for seg in re.split(r";\s+|\s+\|\s+", t):
        seg = seg.strip()
        if not seg:
            continue
        # "UMG Cuts Jobs, Def Jam CEO Tunji Balogun Exits" -> two independent clauses
        m = SECOND_SUBJECT.match(seg)
        if m:
            left, right = m.group("left"), m.group("right")
            lr, rr = _parse_one(left), _parse_one(right)
            if lr and rr:
                out += [lr, rr]
                continue
        # "Interscope names A President, promotes B and C to Co-Presidents" -> same subject, two verbs
        clauses = CLAUSE_SPLIT.split(seg)
        subject = None
        for i, c in enumerate(clauses):
            c = c.strip(" ,")
            if i > 0 and subject and re.match(rf"^(?:{HIRE_VERBS}|{PROMO_VERBS}|{LEAVE_VERBS_COMPANY})\b", c, re.I):
                c = f"{subject} {c}"
            rs = _parse_many(c)
            if i == 0:
                if rs:
                    subject = rs[0].get("subject") or None
                elif len(clauses) > 1:
                    vm = re.match(rf"^(?P<subj>.+?)\s+(?:{HIRE_VERBS}|{PROMO_VERBS}|{LEAVE_VERBS_COMPANY})\b", c, re.I)
                    if vm:
                        subject = clean_company(vm.group("subj"))
                    elif len(c.split()) <= 7:
                        subject = clean_company(c)  # "Ticketmaster Goes All-In on AI, Names ..." -> "Ticketmaster"
                        continue
            out += rs
    for r in out:
        r.pop("subject", None)
    return out


def _parse_many(t: str) -> list[dict]:
    """Like _parse_one but expands 'Company promotes A and B to X' into one entry per person."""
    r = _parse_one(t)
    if not r:
        return []
    names = r.pop("_names", None)
    if names and len(names) > 1:
        return [dict(r, name=n) for n in names]
    return [r]


def _parse_one(t: str) -> dict | None:
    t = t.strip()
    # drop appositives: "Hires Lauren Glaubach, Former YouTube and Pinterest Exec, to Lead ..."
    t = re.sub(r",\s*(?:former|ex-|a |an |the |who |longtime|veteran)[^,]*,", "", t, flags=re.I)

    # ---- layoffs / restructuring (company-level, usually no person)
    m = re.match(rf"^(?P<company>[A-Z][\w&'.-]*(?:\s+[\w&'.-]+){{0,5}}?)\s+(?:{LAYOFF})\b(?P<rest>.*)$", t, re.I)
    if (
        m
        and re.search(r"\b(staff|jobs|employees|workforce|positions|roles|headcount|layoffs?|team|%|percent|restructur|shake-?up|cuts)\b", t, re.I)
        and not re.search(r"\b(ties with|ties to)\b", t, re.I)
        and not re.search(rf"\b(?:{HIRE_VERBS}|{PROMO_VERBS})\b", m.group("company"), re.I)
    ):
        return _mk("laid_off", "", "", m.group("company")) | {"subject": clean_company(m.group("company"))}
    m = re.match(r"^(?:layoffs?|job cuts)\s+(?:hit|at|coming to|loom at|sweep|reported at)\s+(?P<company>.+?)(?:[,:]|\s+as\b|\s+amid\b|$)", t, re.I)
    if m:
        return _mk("laid_off", "", "", m.group("company"))

    # ---- "Company Position Name Named/Exits ..." e.g. "Republic Records CEO Jim Roppo Named Vice Chairman at Warner Records Group"
    m = re.match(rf"^(?P<company>[A-Z][\w&'.-]*(?:\s+[\w&'.-]+){{0,5}}?)\s+(?P<oldpos>(?i:{TITLES}))\s+(?P<rest>[A-Z].*)$", t)
    if m and not TITLE_WORD.match(m.group("company").split()[-1]):
        name, rest = first_name_in(m.group("rest"))
        mm = re.match(rf"^(?:is |has |was |to be |will be )?(?P<verb>{PASSIVE_HIRE}|{PASSIVE_PROMO}|{LEAVE_VERBS_PERSON})\b(?P<rest>.*)$", rest, re.I) if name else None
        if mm:
            verb = mm.group("verb").lower()
            company = m.group("company")
            if re.match(rf"^{LEAVE_VERBS_PERSON}$", verb, re.I):
                return _mk("departed", name, m.group("oldpos"), company)
            pos, comp = split_position_company(mm.group("rest"))
            comp = comp or company
            action = "promoted" if (re.match(rf"^{PASSIVE_PROMO}$", verb, re.I) or comp.lower() == company.lower()) else "hired"
            r = _mk(action, name, pos, comp)
            r["previous"] = f"{m.group('oldpos')} at {clean_company(company)}"
            return r

    # ---- "Name Named/Appointed/Promoted [as/to] Position at Company"
    names, rest = leading_names(t)
    name = names[-1] if names else ""
    if name:
        mm = re.match(rf"^(?:has been |have been |is |are |was |were |to be |will be )?(?P<verb>{PASSIVE_HIRE}|{PASSIVE_PROMO})\s+(?P<rest>.+)$", rest, re.I)
        if mm:
            pos, comp = split_position_company(mm.group("rest"))
            action = "promoted" if re.match(rf"^{PASSIVE_PROMO}", mm.group("verb"), re.I) else "hired"
            return _mk(action, name, pos, comp) | {"_names": names}

        # ---- "Name Joins Company as Position"
        mm = re.match(r"^(?:to )?(?:joins|join|lands at|moves to|heads to|returns to|rejoins|signs on at|takes over at|takes over as|takes helm at|takes the helm at|becomes|is now|now at|to join|has joined)\s+(?P<rest>.+)$", rest, re.I)
        if mm:
            r2 = mm.group("rest")
            m3 = re.match(r"^(?P<company>.+?)\s+(?:as|to become|to lead|to head|to run|to oversee|in)\s+(?P<pos>.+)$", r2, re.I)
            if m3:
                return _mk("hired", name, m3.group("pos"), m3.group("company"))
            return _mk("hired", name, "", r2)

        # ---- "Name Exits/Leaves/Steps Down [as Position] [at/from Company]"
        mm = re.match(rf"^(?:is |has |to |will )?(?P<verb>{LEAVE_VERBS_PERSON})\b(?P<rest>.*)$", rest, re.I)
        if mm:
            r2 = mm.group("rest").strip()
            pos, comp = "", ""
            m3 = re.match(r"^(?:as|from (?:his|her|their) (?:role|post|position) as|from (?:the )?(?:role|post) of)\s+(?P<rest>.+)$", r2, re.I)
            if m3:
                pos, comp = split_position_company(m3.group("rest"))
            else:
                m3 = re.match(r"^(?:from|at)\s+(?P<comp>.+?)(?:\s+as\s+(?P<pos>.+))?$", r2, re.I)
                if m3:
                    comp, pos = m3.group("comp"), m3.group("pos") or ""
                elif r2 and r2[0].isupper():
                    comp = r2  # "Exits Sony Music Publishing to Launch ..."
            return _mk("departed", name, pos, comp)

        # ---- "Name Retires From Company After N Years"
        mm = re.match(r"^(?:to )?(?:retires?|retiring)\s+(?:from|at)\s+(?P<company>.+)$", rest, re.I)
        if mm:
            return _mk("departed", name, "", mm.group("company"))

        # ---- "Name Expands Duties as Company's Position" / "Name Takes On Position at Company"
        mm = re.match(r"^(?:expands (?:duties|role|remit)|takes on (?:expanded|new|added) (?:role|duties)|adds [\w ]+ to (?:his|her|their) (?:role|duties|remit)|to oversee|to lead|to head|will lead|now leads|takes on|takes over)\s*(?:as|at|of)?\s*(?P<rest>.*)$", rest, re.I)
        if mm:
            r2 = mm.group("rest")
            m3 = re.match(r"^(?P<company>[A-Z][\w&.-]*(?:\s+[A-Z][\w&.-]*)*)'s\s+(?P<pos>.+)$", r2)
            if m3:
                return _mk("promoted", name, m3.group("pos"), m3.group("company"))
            pos, comp = split_position_company(r2)
            return _mk("promoted", name, pos, comp)

        # ---- "Name launches Company"  (people starting their own shop; still a move)
        mm = re.match(r"^(?:launches|launch|forms|founds|starts|opens|has launched|to launch)\s+(?P<company>.+?)(?:\s+(?:with|in partnership|after|following|,).*)?$", rest, re.I)
        if mm and re.search(r"\b(label|records|agency|firm|company|venture|management|publishing|imprint|shop|group|media|marketing)\b", t, re.I):
            return _mk("other", name, "founder", mm.group("company"))

    # ---- "Former Virgin Music Comms Head Liz Morentin Launches ..."
    if re.match(r"^(?:former|ex-|outgoing|longtime|veteran)\b", t, re.I):
        toks = t.split()
        for i in range(1, len(toks) - 1):
            name, rest = first_name_in(" ".join(toks[i:]))
            if name and rest:
                r = _parse_one(f"{name} {rest}")
                if r:
                    r["previous"] = " ".join(toks[1:i]).strip(" ,")
                    return r

    # ---- "Company Ousts/Fires Name [as Position]"
    m = re.match(rf"^(?P<company>.+?)\s+(?P<verb>{LEAVE_VERBS_COMPANY})\s+(?P<rest>.+)$", t, re.I)
    if m:
        rest = m.group("rest")
        pm = re.match(rf"^(?P<pre>{TITLES})\s+(?P<rest>[A-Z].*)$", rest)
        pos = ""
        if pm:
            pos, rest = pm.group("pre"), pm.group("rest")
        name, after = first_name_in(rest)
        if name:
            am = re.match(r"^as\s+(.+)$", after)
            if am:
                pos = am.group(1)
            return _mk("departed", name, pos, m.group("company"))

    # ---- "Company Names/Appoints/Hires/Promotes [descriptor] Name [as/to] Position"
    m = re.match(rf"^(?P<company>.+?)\s+(?P<verb>{HIRE_VERBS}|{PROMO_VERBS})\s+(?P<rest>.+)$", t, re.I)
    if m:
        verb = m.group("verb").lower()
        company = m.group("company")
        rest = m.group("rest")
        action = "promoted" if re.match(rf"^{PROMO_VERBS}$", verb, re.I) else "hired"
        strong = bool(STRONG_HIRE.match(verb)) and not verb.startswith("name")
        # Roundup: "Interscope Promotes Three", "Ticketmaster Hires Three"
        if re.match(r"^(?:two|three|four|five|six|seven|eight|\d+)(?:\s+(?:new\s+)?(?:execs?|executives|hires|people|leaders|staffers|promotions|appointments|senior\s+\w+|vps?|presidents|directors|managers|members))?(?:\s+(?:amid|after|in|to|as|following|at|across)\b|$)", rest, re.I):
            return _mk(action, "", "", company) | {"roundup": True, "subject": clean_company(company)}
        # "... Name as Position" / "... Name to Position"
        mm = re.match(r"^(?P<pre>.+?)\s+(?:as|to|to the role of|to the post of|to the position of)\s+(?P<pos>.+)$", rest, re.I)
        if mm:
            names = split_names(mm.group("pre"))
            if names:
                pos, comp2 = split_position_company(mm.group("pos"))
                comp = company
                if comp2 and comp2.lower() != comp.lower() and comp2.lower() not in comp.lower():
                    comp = f"{comp2} ({clean_company(comp)})"
                # "Tapped as Arista Records Co-Presidents" -> company from the position
                pm = re.match(rf"^(?P<comp>[A-Z][\w&'.-]*(?:\s+[A-Z][\w&'.-]*)*)\s+(?P<pos>{TITLES}.*)$", pos)
                if pm and looks_like_company(pm.group("comp")):
                    comp, pos = pm.group("comp"), pm.group("pos")
                r = _mk(action, names[-1], pos, comp) | {"subject": clean_company(company)}
                r["_names"] = names
                return r
            # "Names Ex-Amazon Exec as Chief Technology Officer" - no name in the headline
            pos, comp2 = split_position_company(mm.group("pos"))
            if TITLE_RE.search(pos):
                return _mk(action, "", pos, comp2 or company) | {"subject": clean_company(company), "previous": mm.group("pre")}
        # "AEG Presents Names Chris Hammond Senior Vice President Of ..." - name then title, no 'as'
        names, after = leading_names(rest)
        name = names[-1] if names else ""
        if name and after and TITLE_RE.search(after) and not re.match(r"^(?:and|&|,)", after):
            return _mk(action, name, after, company) | {"subject": clean_company(company), "_names": names}
        # "Company Names New CEO" - nobody named in the headline
        mm = re.match(rf"^(?:new |its new |a new |its next |next )?(?P<pos>(?:{TITLES})\b.*)$", rest, re.I)
        if mm and not name:
            return _mk(action, "", mm.group("pos"), company) | {"subject": clean_company(company)}
        # "Peermusic Promotes Julio Bagué and Lalo Molina, Strengthening ..."
        head = re.split(r",\s+|\s+(?:amid|after|following|in|for|ahead of)\b", rest, maxsplit=1)[0]
        names = split_names(head)
        if names and (strong or TITLE_RE.search(rest)):
            r = _mk(action, names[-1], "", company) | {"subject": clean_company(company)}
            r["_names"] = names
            return r

    return None


# --------------------------------------------------------------------------------------
# Fetching
# --------------------------------------------------------------------------------------

def get(url: str) -> requests.Response | None:
    for attempt in range(3):
        try:
            r = requests.get(url, headers=HEADERS, timeout=TIMEOUT)
            if r.status_code == 200:
                return r
            if r.status_code in (403, 404):
                return None
        except requests.RequestException:
            pass
        time.sleep(1.5 * (attempt + 1))
    return None


def entry_date(e) -> datetime | None:
    for key in ("published", "updated"):
        v = e.get(key)
        if v:
            try:
                d = parsedate_to_datetime(v)
                return d if d.tzinfo else d.replace(tzinfo=timezone.utc)
            except (TypeError, ValueError):
                pass
    for key in ("published_parsed", "updated_parsed"):
        v = e.get(key)
        if v:
            return datetime(*v[:6], tzinfo=timezone.utc)
    return None


def entry_summary(e) -> str:
    raw = ""
    if e.get("content"):
        raw = e["content"][0].get("value", "")
    raw = raw or e.get("summary", "") or ""
    return BeautifulSoup(raw, "html.parser").get_text(" ", strip=True)


def fetch_feed(name: str, url: str, since: datetime, music_filter: bool) -> list[dict]:
    r = get(url)
    if r is None:
        print(f"  ! {name}: unreachable", file=sys.stderr)
        return []
    parsed = feedparser.parse(r.content)
    items = []
    for e in parsed.entries:
        d = entry_date(e)
        if d and d < since:
            continue
        title = e.get("title", "").strip()
        link = e.get("link", "").strip()
        if not title or not link:
            continue
        summary = entry_summary(e)
        if music_filter and not looks_like_music(title, summary):
            continue
        if not looks_like_move(title, summary, broad=music_filter):
            continue
        items.append({
            "source": name,
            "title": title,
            "url": link,
            "date": d.date().isoformat() if d else "",
            "summary": summary[:600],
        })
    return items


def expand_billboard_list(item: dict) -> list[dict]:
    """Billboard 'Executive Turntable' columns are lists; each entry is its own move."""
    r = get(item["url"])
    if r is None:
        return [item]
    soup = BeautifulSoup(r.text, "html.parser")
    entries = soup.select("article.pmc-fallback-list-item")
    out = []
    for art in entries:
        h = art.find(["h2", "h3"])
        p = art.find("p")
        if not h:
            continue
        head = h.get_text(" ", strip=True)
        body = p.get_text(" ", strip=True) if p else ""
        # Billboard list entries render the first letter as a drop cap, so "W arner" -> "Warner"
        body = re.sub(r"^([A-Z]) (?=[a-z])", r"\1", body)
        out.append({**item, "title": head, "summary": body[:800], "list_headline": item["title"]})
    return out or [item]


def parse_list_entry(title: str, body: str) -> list[dict]:
    """Billboard list entries look like 'Ron Broitman (Warner Music Group)' with the
    details in the first paragraph. Prefer the paragraph's first sentence."""
    m = re.match(r"^(?P<name>[^()]+?)\s*\((?P<company>[^)]+)\)\s*$", title)
    if not m:
        return parse_headline(title)
    name, company = m.group("name").strip(), m.group("company").strip()
    first = re.split(r"(?<=[.!?])\s+", norm(body))[0] if body else ""
    found = _parse_one(first) if first else None
    if found and found.get("name") and found["name"].split()[-1].lower() in name.lower():
        found.pop("_names", None)
        found.pop("subject", None)
        found["company"] = found["company"] or company
        # "general manager of Position Music" -> "general manager"
        found["position"] = re.sub(rf"\s+(?:of|at)\s+{re.escape(found['company'])}$", "", found["position"], flags=re.I)
        return [found]
    # fall back to the title's name/company and search the sentence for a title phrase
    action = "hired"
    low = first.lower()
    if re.search(r"\b(launch|launched|launches|founded|founds|forms|formed|opens|opened)\b", low):
        action, pos = "other", "founder"
        return [_mk(action, name, pos, company)]
    if re.search(r"\b(promoted|promotes|promotion|elevated|elevates|upped|expanded|expands)\b", low):
        action = "promoted"
    elif re.search(r"\b(exit|exits|exited|leaves|leaving|left|depart|departs|departed|step(s|ped)? down|retire|retires|retired|resign|resigns|resigned|ousted|fired|laid off)\b", low):
        action = "departed"
    pos = ""
    mm = re.search(rf"\b(?:as|to|named|appointed|promoted to|becomes?|is now|role of|post of|position of)\s+(?:the |its |a |an )?((?:{TITLES})\b[^.;]*)", first, re.I)
    if mm:
        pos = mm.group(1)
        pos = re.sub(rf"\s+(?:of|at)\s+(?:the company|{re.escape(company)})\b.*$", "", pos, flags=re.I)
        pos = re.split(r",\s+(?:the|a|an|effective|reporting|where|who|based)\b", pos, maxsplit=1)[0]
    return [_mk(action, name, pos, company)]


# --------------------------------------------------------------------------------------
# Optional: Claude extraction
# --------------------------------------------------------------------------------------

LLM_SCHEMA = {
    "type": "object",
    "properties": {
        "moves": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "item_id": {"type": "integer"},
                    "name": {"type": "string"},
                    "company": {"type": "string"},
                    "position": {"type": "string"},
                    "previous": {"type": "string"},
                    "action": {"type": "string", "enum": ["hired", "promoted", "departed", "laid_off", "other"]},
                },
                "required": ["item_id", "name", "company", "position", "previous", "action"],
                "additionalProperties": False,
            },
        }
    },
    "required": ["moves"],
    "additionalProperties": False,
}

LLM_PROMPT = """You read music-industry trade press and log personnel moves.

For each numbered item below (headline, outlet, and a snippet), list every person whose job changed:
hired (joined from outside), promoted (new title at the same company), departed (exit, step down,
retire, ousted, fired), laid_off (company-level cuts; name may be empty), or other.
Give the person's full name, the company the move is at, the new position (for departures, the
position they are leaving), and "previous" (prior role/company if stated, else empty string).
One entry per person; an item can produce several entries or none. Skip items that are not
personnel moves (album releases, lawsuits, partnerships, tours).

Items:
"""


def llm_extract(items: list[dict], model: str) -> dict[int, list[dict]]:
    import anthropic  # optional dependency

    client = anthropic.Anthropic()
    results: dict[int, list[dict]] = {}
    batch = 25
    for start in range(0, len(items), batch):
        chunk = items[start:start + batch]
        lines = []
        for i, it in enumerate(chunk):
            lines.append(f"{start + i}. [{it['source']}] {norm(it['title'])}\n   {norm(it.get('summary', ''))[:500]}")
        resp = client.beta.messages.create(
            model=model,
            max_tokens=16000,
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
            output_config={"format": {"type": "json_schema", "schema": LLM_SCHEMA}},
            messages=[{"role": "user", "content": LLM_PROMPT + "\n".join(lines)}],
        )
        if resp.stop_reason == "refusal":
            print("  ! Claude declined a batch; falling back to rules for it", file=sys.stderr)
            continue
        text = next(b.text for b in resp.content if b.type == "text")
        for mv in json.loads(text)["moves"]:
            results.setdefault(mv["item_id"], []).append(mv)
    return results


# --------------------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------------------

def load_state(path: str | None) -> set[str]:
    if not path or not os.path.exists(path):
        return set()
    with open(path) as f:
        return set(json.load(f).get("seen", []))


def save_state(path: str | None, seen: set[str]) -> None:
    if not path:
        return
    with open(path, "w") as f:
        json.dump({"seen": sorted(seen), "updated": datetime.now(timezone.utc).isoformat()}, f, indent=0)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--days", type=int, default=7, help="look back this many days (default 7)")
    ap.add_argument("--format", choices=["table", "csv", "json"], default="table")
    ap.add_argument("--out", help="write output here instead of stdout")
    ap.add_argument("--state", help="JSON file of already-seen URLs; only new stories are reported")
    ap.add_argument("--no-google", action="store_true", help="skip Google News searches")
    ap.add_argument("--no-expand", action="store_true", help="don't open Billboard roundup pages for the full list")
    ap.add_argument("--all", action="store_true", help="include items that matched but nothing could be extracted")
    ap.add_argument("--llm", action="store_true", help="use Claude for extraction (needs ANTHROPIC_API_KEY)")
    ap.add_argument("--model", default="claude-opus-5-5", help="model for --llm (default claude-opus-5-5)")
    ap.add_argument("--only", help="comma-separated substrings; keep only sources whose name matches")
    args = ap.parse_args()

    since = datetime.now(timezone.utc) - timedelta(days=args.days)
    seen = load_state(args.state)

    sources = [(n, u, f) for n, u, f in FEEDS]
    if not args.no_google:
        sources += [(f"Google News: {q[:50]}", google_news_url(q, args.days), True) for q in GOOGLE_NEWS_QUERIES]
    if args.only:
        keys = [k.strip().lower() for k in args.only.split(",")]
        sources = [s for s in sources if any(k in s[0].lower() for k in keys)]

    print(f"Fetching {len(sources)} sources, last {args.days} days...", file=sys.stderr)
    items: list[dict] = []
    for name, url, music_filter in sources:
        got = fetch_feed(name, url, since, music_filter)
        print(f"  {name}: {len(got)}", file=sys.stderr)
        items += got

    # dedupe by URL (Google News wraps links, so also dedupe by normalized title)
    uniq: dict[str, dict] = {}
    for it in items:
        key = re.sub(r"\s+-\s+[a-z][\w .&'-]{2,40}$", "", norm(it["title"]).lower())
        key = re.sub(r"\W+", " ", key).strip()
        if key in uniq:
            continue
        uniq[key] = it
    items = list(uniq.values())
    new_items = [it for it in items if it["url"] not in seen]
    print(f"{len(items)} candidate stories, {len(new_items)} not seen before", file=sys.stderr)

    # expand Billboard roundup columns into their individual entries
    expanded: list[dict] = []
    for it in new_items:
        if not args.no_expand and "billboard.com/lists/" in it["url"]:
            expanded += expand_billboard_list(it)
        else:
            expanded.append(it)

    moves: list[Move] = []
    llm_results = {}
    if args.llm:
        try:
            llm_results = llm_extract(expanded, args.model)
        except Exception as ex:  # noqa: BLE001 - report and continue with rules
            print(f"  ! LLM extraction failed ({ex}); using rules", file=sys.stderr)

    for idx, it in enumerate(expanded):
        if idx in llm_results:
            found = llm_results[idx]
            conf = "llm"
        else:
            conf = "rule"
            if it.get("list_headline"):
                found = parse_list_entry(it["title"], it.get("summary", ""))
            else:
                found = parse_headline(it["title"])
        if not found and args.all:
            found = [_mk("unknown")]
        for f in found:
            if not args.all and not (f.get("name") or f.get("company")):
                continue
            extra = {k: v for k, v in f.items() if k not in ("action", "name", "position", "company")}
            if it.get("list_headline"):
                extra["column"] = it["list_headline"]
            moves.append(Move(
                date=it.get("date", ""), action=f["action"], name=f.get("name", ""),
                position=f.get("position", ""), company=f.get("company", ""),
                headline=norm(it["title"]), source=it["source"].split(":")[0], url=it["url"],
                confidence=conf, extra=extra,
            ))

    # the same move is often reported by several outlets; keep the most complete record
    moves.sort(key=lambda m: (m.date, bool(m.position) + bool(m.company) + bool(m.name)), reverse=True)
    kept: dict[tuple, Move] = {}
    for m in moves:
        key = (m.name.lower(), m.action) if m.name else (m.company.lower(), m.action, m.headline.lower())
        if key not in kept:
            kept[key] = m
    moves = sorted(kept.values(), key=lambda m: (m.date, m.company.lower(), m.name), reverse=True)

    out = open(args.out, "w", newline="") if args.out else sys.stdout
    try:
        if args.format == "json":
            json.dump([asdict(m) for m in moves], out, indent=2, ensure_ascii=False)
            out.write("\n")
        elif args.format == "csv":
            w = csv.writer(out)
            w.writerow(["date", "action", "name", "position", "company", "headline", "source", "url", "confidence"])
            for m in moves:
                w.writerow([m.date, m.action, m.name, m.position, m.company, m.headline, m.source, m.url, m.confidence])
        else:
            if not moves:
                out.write("No new moves found.\n")
            for m in moves:
                who = m.name or "(unnamed)"
                pos = f" - {m.position}" if m.position else ""
                out.write(f"{m.date}  {m.action:<9} {who}{pos} @ {m.company}\n")
                out.write(f"            {m.headline}\n            {m.source} | {m.url}\n")
    finally:
        if out is not sys.stdout:
            out.close()

    if args.state:
        seen |= {it["url"] for it in new_items}
        save_state(args.state, seen)
    print(f"{len(moves)} moves extracted from {len(expanded)} stories", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
