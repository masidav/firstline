#!/usr/bin/env python3
"""Find trivia about bands and artists and turn it into multiple-choice questions.

    python3 band_trivia.py Paramore Radiohead "Fleetwood Mac"
    python3 band_trivia.py --file artists.txt --format table
    python3 band_trivia.py spotify:74XFHRwlV6OrjEM0A2NCMF     # by Spotify artist ID (exact match)
    python3 band_trivia.py Q473466                            # by Wikidata ID
    python3 band_trivia.py Paramore --llm                     # Claude also writes questions from prose facts

Sources, all openly licensed:
  - Wikidata (CC0): formation year and place, members, labels, genres, awards, birth details.
  - MusicBrainz (CC0 core data): studio albums with release dates, begin date and area (a cross-check).
  - Wikipedia (CC BY-SA 4.0): prose sentences that read like trivia ("named after", "originally called",
    "first band to", "met at"). Reword before publishing, or credit Wikipedia.

Questions use the app's TriviaQuestion fields (kind, prompt, options, answer_index). A question is
`verified` only when two independent sources agree on the answer (for example Wikidata and MusicBrainz
on the formation year). Questions where the sources disagree are skipped and listed under `skipped`.
Distractors come from comparable artists (same genre and country) and are dropped if they appear anywhere
in the artist's Wikipedia article, so a wrong option is unlikely to be secretly right. Staff should still
read every question before it ships.
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import random
import re
import sys
import time
from datetime import datetime, timezone

import requests

CONTACT = os.environ.get("CRAWLER_CONTACT", "")
UA = "firstline-crawlers/0.1 (band trivia research script" + (f"; {CONTACT}" if CONTACT else "") + ")"
SESSION = requests.Session()
SESSION.headers.update({"User-Agent": UA, "Accept": "application/json", "Accept-Encoding": "gzip, deflate"})

WD_API = "https://www.wikidata.org/w/api.php"
SPARQL = "https://query.wikidata.org/sparql"
MB = "https://musicbrainz.org/ws/2"
WP_API = "https://en.wikipedia.org/w/api.php"

HUMAN = "Q5"
US = "Q30"
US_STATE = "Q35657"
THIS_YEAR = datetime.now(timezone.utc).year

# --------------------------------------------------------------------------------------
# HTTP
# --------------------------------------------------------------------------------------

_last_mb = 0.0


def get_json(url: str, params: dict, *, retries: int = 4) -> dict | None:
    global _last_mb
    for attempt in range(retries):
        if url.startswith(MB):  # MusicBrainz allows one request per second
            wait = 1.1 - (time.monotonic() - _last_mb)
            if wait > 0:
                time.sleep(wait)
            _last_mb = time.monotonic()
        try:
            r = SESSION.get(url, params=params, timeout=60)
        except requests.RequestException:
            time.sleep(2 ** attempt)
            continue
        if r.status_code == 200:
            return r.json()
        if r.status_code == 404:
            return None
        if r.status_code in (429, 500, 502, 503, 504):
            time.sleep(float(r.headers.get("Retry-After", 0)) or 2 ** attempt)
            continue
        return None
    return None


def sparql(query: str) -> list[dict]:
    data = get_json(SPARQL, {"query": query, "format": "json"})
    if not data:
        return []
    rows = []
    for b in data["results"]["bindings"]:
        rows.append({k: v["value"].rsplit("/", 1)[-1] if v["value"].startswith("http://www.wikidata.org/entity/") else v["value"]
                     for k, v in b.items()})
    return rows


# --------------------------------------------------------------------------------------
# Wikidata
# --------------------------------------------------------------------------------------

def entities(ids: list[str], props: str = "labels") -> dict:
    out = {}
    ids = [i for i in dict.fromkeys(ids) if i]
    for i in range(0, len(ids), 50):
        data = get_json(WD_API, {"action": "wbgetentities", "ids": "|".join(ids[i:i + 50]), "props": props,
                                 "languages": "en|mul", "sitefilter": "enwiki", "format": "json"})
        if data:
            out.update(data.get("entities", {}))
    return out


def label_of(ent: dict) -> str:
    labels = ent.get("labels", {})
    return (labels.get("en") or labels.get("mul") or {}).get("value", "")


def resolve(query: str) -> dict | None:
    """Name, Wikidata ID, or spotify:<artist id> -> Wikidata entity with claims."""
    query = query.strip()
    if re.fullmatch(r"Q\d+", query):
        qid = query
    else:
        if query.lower().startswith("spotify:"):
            search = f"haswbstatement:P1902={query.split(':', 1)[1]}"
        else:
            # P434 = MusicBrainz artist ID: limits the search to music artists
            search = f"{query} haswbstatement:P434"
        data = get_json(WD_API, {"action": "query", "list": "search", "srsearch": search, "srlimit": 7, "format": "json"})
        hits = [h["title"] for h in (data or {}).get("query", {}).get("search", [])]
        if not query.lower().startswith("spotify:"):
            # label-ranked search finds "Radiohead" the band before members whose pages mention it
            data = get_json(WD_API, {"action": "wbsearchentities", "search": query, "language": "en", "type": "item",
                                     "limit": 10, "format": "json"})
            hits = [h["id"] for h in (data or {}).get("search", [])] + hits
        if not hits:
            return None
        ents = entities(hits, "labels|claims")
        music = [h for h in dict.fromkeys(hits) if "P434" in ents.get(h, {}).get("claims", {})]
        if not music:
            return None
        exact = [h for h in music if label_of(ents[h]).casefold() == query.casefold()]
        qid = (exact or music)[0]
    ent = entities([qid], "labels|descriptions|claims|sitelinks/urls").get(qid)
    return ent if ent and "missing" not in ent else None


def claims(ent: dict, prop: str, best: bool = False) -> list[dict]:
    cs = [c for c in ent.get("claims", {}).get(prop, []) if c.get("rank") != "deprecated"
          and c["mainsnak"].get("snaktype") == "value"]
    if best:
        pref = [c for c in cs if c.get("rank") == "preferred"]
        cs = pref or cs
    return cs


def item_ids(ent: dict, prop: str, best: bool = False) -> list[str]:
    return [c["mainsnak"]["datavalue"]["value"]["id"] for c in claims(ent, prop, best)
            if c["mainsnak"]["datavalue"]["type"] == "wikibase-entityid"]


def string_vals(ent: dict, prop: str) -> list[str]:
    out = []
    for c in claims(ent, prop):
        v = c["mainsnak"]["datavalue"]["value"]
        out.append(v["text"] if isinstance(v, dict) and "text" in v else str(v))
    return out


def year_of(ent: dict, prop: str) -> int | None:
    for c in claims(ent, prop, best=True):
        v = c["mainsnak"]["datavalue"]["value"]
        if v.get("precision", 0) >= 9:
            m = re.match(r"^\+?(-?\d+)-", v["time"])
            if m:
                return int(m.group(1))
    return None


def qualifier_year(claim: dict, prop: str = "P585") -> int | None:
    for q in claim.get("qualifiers", {}).get(prop, []):
        m = re.match(r"^\+?(\d{4})-", q.get("datavalue", {}).get("value", {}).get("time", ""))
        if m:
            return int(m.group(1))
    return None


BUILDING = re.compile(r"hospital|maternity|clinic|infirmary|medical cent|nursing home|\bstreet\b|\broad\b|\bavenue\b|\bgrove\b|\blane\b|^\d", re.I)


def place_names(qids: list[str]) -> dict[str, tuple[str, str]]:
    """City -> ('Franklin, Tennessee', country id) in the US, ('Abingdon, United Kingdom', country id) elsewhere.
    Birthplaces recorded as a hospital or street address are shown as the town they are in."""
    qids = [q for q in dict.fromkeys(qids) if q]
    out: dict[str, tuple[str, str]] = {}
    for i in range(0, len(qids), 60):
        chunk = qids[i:i + 60]
        rows = sparql(f"""
SELECT ?p ?pLabel ?townLabel ?stateLabel ?countryLabel ?country WHERE {{
  VALUES ?p {{ {' '.join('wd:' + q for q in chunk)} }}
  OPTIONAL {{ ?p wdt:P17 ?country }}
  OPTIONAL {{ ?p wdt:P131 ?town }}
  OPTIONAL {{ ?p wdt:P131/wdt:P131?/wdt:P131? ?state . ?state wdt:P31 wd:{US_STATE} }}
  SERVICE wikibase:label {{ bd:serviceParam wikibase:language "en,mul". }}
}}""")
        for r in rows:
            p, name = r["p"], r.get("pLabel", "")
            if not name or re.fullmatch(r"Q\d+", name) or p in out:
                continue
            if BUILDING.search(name):
                town = r.get("townLabel", "")
                if not town or re.fullmatch(r"Q\d+", town) or BUILDING.search(town):
                    continue
                name = town
            region = r.get("stateLabel") if r.get("country") == US and r.get("stateLabel") else r.get("countryLabel", "")
            out[p] = (f"{name}, {region}" if region and region != name else name, r.get("country", ""))
    return out


# --------------------------------------------------------------------------------------
# MusicBrainz and Wikipedia
# --------------------------------------------------------------------------------------

def mb_artist(mbid: str) -> dict:
    return get_json(f"{MB}/artist/{mbid}", {"fmt": "json"}) or {}


def mb_studio_albums(mbid: str) -> list[tuple[str, str]]:
    """[(first_release_date, title)] for albums with no secondary type (no live, compilation, remix...)."""
    out, offset = [], 0
    while True:
        data = get_json(f"{MB}/release-group", {"artist": mbid, "type": "album", "limit": 100, "offset": offset, "fmt": "json"})
        if not data:
            break
        for rg in data.get("release-groups", []):
            if rg.get("primary-type") == "Album" and not rg.get("secondary-types") and rg.get("first-release-date"):
                out.append((rg["first-release-date"], rg["title"]))
        offset += 100
        if offset >= data.get("release-group-count", 0):
            break
    out.sort()
    # MusicBrainz sometimes has the same album twice (regional titles); keep the first of each title
    seen, uniq = set(), []
    for d, t in out:
        k = re.sub(r"\W+", "", t.casefold())
        if k not in seen:
            seen.add(k)
            uniq.append((d, t))
    return uniq


def wikipedia(title: str) -> tuple[str, str]:
    data = get_json(WP_API, {"action": "query", "prop": "extracts", "explaintext": 1, "titles": title,
                             "redirects": 1, "format": "json"})
    if not data:
        return "", ""
    page = next(iter(data["query"]["pages"].values()))
    url = "https://en.wikipedia.org/wiki/" + page.get("title", title).replace(" ", "_")
    return page.get("extract", ""), url


TRIVIA_CUES = [
    (3, r"named (?:themselves |the band |it )?after|(?:band'?s?|group'?s?|their|the) name (?:came|comes|was taken|derives|derived|is taken|is a reference|refers)|took (?:its|their) name|originally (?:called|named|known as|performed as)|formerly known as|changed (?:their|its) name|went by the name|stage name"),
    (3, r"world record|guinness|first (?:band|act|artist|group|female|woman|british|american|uk|us)\b[^.]{0,40}\bto\b|only (?:band|act|artist|song|album) (?:to|ever|in)|the only song (?:written|recorded)"),
    (2, r"\bmet (?:at|in|while|through|when|during)|high school|at school|childhood friends?|before forming|day jobs?|worked as an?|answered an? (?:ad|advert)|audition"),
    (2, r"first (?:show|gig|concert|song|demo|rehearsal|performance)\b|wrote [^.]{0,40} in (?:\w+ )?(?:minutes|hours|an afternoon|one night)|recorded [^.]{0,40} in (?:\w+ )?(?:days|hours|a week)"),
    (1, r"inspired by|nicknamed|youngest|oldest|longest|best-selling|sold more than|inducted|hall of fame|banned|cameo|appeared in"),
]
CUE_RES = [(w, re.compile(p, re.I)) for w, p in TRIVIA_CUES]
RELEASE_DETAIL = re.compile(r"\b(?:single|singles|chart|charts|charted|billboard 200|hot 100|peaked|debuted at|certified|streams|was released (?:on|in))\b", re.I)
SKIP_SECTIONS = re.compile(r"^(references|external links|see also|notes|further reading|discography|band members|members|bibliography|filmography|tours|awards and nominations)$", re.I)


def prose_facts(text: str, limit: int) -> list[dict]:
    facts, section = [], "Introduction"
    for block in re.split(r"\n+", text):
        h = re.match(r"^=+\s*(.+?)\s*=+$", block.strip())
        if h:
            section = h.group(1)
            continue
        if SKIP_SECTIONS.match(section.split(":")[0].strip()):
            continue
        for s in re.split(r"(?<=[.!?])\s+(?=[A-Z\"“])", block):
            s = s.strip()
            if not (50 <= len(s) <= 350) or not s[0].isupper():
                continue
            score = sum(w for w, rx in CUE_RES if rx.search(s))
            if score and RELEASE_DETAIL.search(s):
                score -= 2
            if score > 0:
                facts.append({"text": s, "section": section, "score": score})
    # best first, but keep article order among equals; drop near-duplicates
    facts.sort(key=lambda f: -f["score"])
    out, seen = [], set()
    for f in facts:
        k = re.sub(r"\W+", " ", f["text"].casefold())[:80]
        if k not in seen:
            seen.add(k)
            out.append(f)
    return out[:limit]


# --------------------------------------------------------------------------------------
# Question building
# --------------------------------------------------------------------------------------

def plain(s: str) -> str:
    return s.replace("’", "'").replace("‘", "'").replace("“", '"').replace("”", '"').replace("–", "-").replace("—", "-")


def mention(text: str, name: str) -> bool:
    """Whole-word, case-insensitive mention of name (or its distinctive part) in text."""
    if not name or not text:
        return False
    text, name = plain(text), plain(name)
    core = re.sub(r"\s*\(.*?\)$", "", name).strip()
    return bool(re.search(rf"(?<!\w){re.escape(core)}(?!\w)", text, re.I))


def mc(rng: random.Random, kind: str, prompt: str, answer: str, distractors: list[str], *, sort: bool = False,
       verified: bool, sources: list[str], note: str = "") -> dict | None:
    opts = []
    for d in distractors:
        if d and d.casefold() != answer.casefold() and d.casefold() not in (o.casefold() for o in opts):
            opts.append(d)
    if len(opts) < 3:
        return None
    options = rng.sample(opts[:8], 3) + [answer]
    if sort:
        options.sort()
    else:
        rng.shuffle(options)
    q = {"kind": kind, "prompt": prompt, "options": options, "answer_index": options.index(answer),
         "answer": answer, "verified": verified, "sources": sources}
    if note:
        q["note"] = note
    return q


def genre_values(genres: list[str]) -> str:
    return " ".join("wd:" + g for g in genres[:4])


def comparable_artists(qid: str, is_person: bool, country: str | None, genres: list[str], limit: int = 40) -> list[str]:
    """Best-known artists of the same kind that share a genre (and country, when known)."""
    kind = "?a wdt:P31 wd:Q5 ." if is_person else "FILTER NOT EXISTS { ?a wdt:P31 wd:Q5 }"
    where = f"?a wdt:{'P27' if is_person else 'P495'} wd:{country} ." if country else ""
    for use_country in ([True, False] if country else [False]):
        rows = sparql(f"""
SELECT ?a (MAX(?sl) AS ?s) WHERE {{
  VALUES ?g {{ {genre_values(genres)} }}
  ?a wdt:P136 ?g ; wdt:P434 [] ; wikibase:sitelinks ?sl .
  {where if use_country else ""}
  {kind}
  FILTER(?a != wd:{qid})
}} GROUP BY ?a ORDER BY DESC(?s) LIMIT {limit}""") if genres else []
        if len(rows) >= 10:
            return [r["a"] for r in rows]
    return [r["a"] for r in rows] if genres else []


def comparable_values(artists: list[str]) -> dict[str, list[tuple[str, str, str]]]:
    """prop -> [(artist, value id, value label)] for members, labels, formation and birth places."""
    out: dict[str, list[tuple[str, str, str]]] = {"P527": [], "P264": [], "P740": [], "P19": []}
    if not artists:
        return out
    rows = sparql(f"""
SELECT ?a ?prop ?x ?xLabel WHERE {{
  VALUES ?a {{ {' '.join('wd:' + a for a in artists)} }}
  VALUES (?prop ?p) {{ ("P527" wdt:P527) ("P264" wdt:P264) ("P740" wdt:P740) ("P19" wdt:P19) }}
  ?a ?p ?x .
  SERVICE wikibase:label {{ bd:serviceParam wikibase:language "en,mul". }}
}}""")
    for r in rows:
        lbl = r.get("xLabel", "")
        if lbl and not re.fullmatch(r"Q\d+", lbl):
            out[r["prop"]].append((r["a"], r["x"], lbl))
    return out


def spread(values: list[tuple[str, str, str]], exclude: set[str]) -> list[tuple[str, str]]:
    """One value per comparable artist first, so distractors don't all come from the same band."""
    seen_artist, seen_val, first, rest = set(), set(), [], []
    for a, x, lbl in values:
        if x in exclude or x in seen_val:
            continue
        seen_val.add(x)
        (rest if a in seen_artist else first).append((x, lbl))
        seen_artist.add(a)
    return first + rest


def year_distractors(rng: random.Random, year: int, avoid: set[int]) -> list[str]:
    pool = [year + d for d in (-4, -3, -2, -1, 1, 2, 3, 4) if year + d <= THIS_YEAR and year + d not in avoid]
    return [str(y) for y in pool]


def wiki_year(text: str, verbs: str) -> int | None:
    """The year in the article's opening ('... formed in Franklin, Tennessee, in 2004')."""
    lead = text.split("\n\n", 1)[0][:1500]
    m = re.search(rf"\b(?:{verbs})\b[^.]{{0,60}}?\b((?:19|20)\d\d)\b", lead, re.I)
    if not m and verbs == "born":
        m = re.search(r"\(born [^)]{0,40}?\b((?:19|20)\d\d)\)", lead)
    return int(m.group(1)) if m else None


def build(query: str, args, rng: random.Random) -> dict:
    ent = resolve(query)
    if not ent:
        return {"query": query, "error": "no music artist found on Wikidata"}
    qid = ent["id"]
    name = label_of(ent)
    is_person = HUMAN in item_ids(ent, "P31")
    wd_url = f"https://www.wikidata.org/wiki/{qid}"
    mbid = (string_vals(ent, "P434") or [None])[0]
    spotify = (string_vals(ent, "P1902") or [None])[0]
    enwiki = ent.get("sitelinks", {}).get("enwiki", {}).get("title")
    genres = item_ids(ent, "P136")

    result = {
        "query": query, "name": name, "description": ent.get("descriptions", {}).get("en", {}).get("value", ""),
        "type": "person" if is_person else "group", "wikidata": qid, "musicbrainz": mbid, "spotify": spotify,
        "questions": [], "facts": [], "skipped": [],
    }
    print(f"  {query} -> {name} ({result['description']}) {wd_url}", file=sys.stderr)

    text, wp_url = wikipedia(enwiki) if enwiki else ("", "")
    mba = mb_artist(mbid) if mbid else {}
    mb_url = f"https://musicbrainz.org/artist/{mbid}" if mbid else ""
    mb_begin = mba.get("life-span", {}).get("begin") or ""
    mb_begin_year = int(mb_begin[:4]) if re.match(r"^\d{4}", mb_begin) else None
    mb_begin_area = (mba.get("begin-area") or {}).get("name", "")

    def add(q, why):
        if q:
            result["questions"].append(q)
        else:
            result["skipped"].append(why)

    # ---- When: formation year (groups) or birth year (people); needs two sources to agree to be verified
    prop, verbs = ("P569", "born") if is_person else ("P571", "formed|founded|formation|formed in|got together")
    by_source = {"Wikidata": year_of(ent, prop), "MusicBrainz": mb_begin_year}
    by_source["Wikipedia"] = wiki_year(text, verbs)
    years = {s: y for s, y in by_source.items() if y}
    if years:
        counts: dict[int, list[str]] = {}
        for s, y in years.items():
            counts.setdefault(y, []).append(s)
        best = max(counts.items(), key=lambda kv: len(kv[1]))
        year, agree = best[0], best[1]
        url_for = {"Wikidata": wd_url, "MusicBrainz": mb_url, "Wikipedia": wp_url}
        if len(counts) > 1 and len(agree) < 2:
            result["skipped"].append("year: sources disagree (" + ", ".join(f"{s} {y}" for s, y in years.items()) + ")")
        else:
            prompt = f"In what year was {name} born?" if is_person else f"In what year did {name} form?"
            note = ""
            if len(counts) > 1:
                note = "Sources differ: " + ", ".join(f"{s} says {y}" for s, y in years.items()) + ". Majority used."
            add(mc(rng, "birth_year" if is_person else "formed_year", prompt, str(year),
                   year_distractors(rng, year, set(years.values()) - {year}), sort=True, verified=len(agree) >= 2,
                   sources=[url_for[s] for s in agree], note=note),
                "year: not enough distractors")

    country = (item_ids(ent, "P27" if is_person else "P495", best=True) or [None])[0]
    peers = comparable_artists(qid, is_person, country, genres)
    peer_vals = comparable_values(peers)

    # ---- Where: formation place (groups) or birthplace (people)
    place_prop = "P19" if is_person else "P740"
    places = item_ids(ent, place_prop, best=True)
    if len(places) == 1:
        cands = [x for x, _ in spread(peer_vals[place_prop], {places[0]})][:30]
        names = place_names([places[0]] + cands)
        if places[0] in names:
            answer, answer_country = names[places[0]]
            city = answer.split(",")[0]
            verified = (mb_begin_area and mb_begin_area.casefold() == city.casefold()) or mention(text[:8000], city)
            ok = [c for c in cands if c in names and names[c][0].split(",")[0].casefold() != city.casefold()
                  and not mention(text, names[c][0].split(",")[0])]
            same_country = [c for c in ok if names[c][1] == answer_country]
            distractors = [names[c][0] for c in (same_country if len(same_country) >= 3 else ok)]
            prompt = f"Where was {name} born?" if is_person else f"Where did {name} form?"
            add(mc(rng, "birthplace" if is_person else "formed_place", prompt, answer, distractors[:15],
                   verified=bool(verified), sources=[wd_url] + ([wp_url] if verified else [])),
                "place: not enough comparable places")
    elif len(places) > 1:
        result["skipped"].append("place: Wikidata lists several places")

    # ---- Who: band members
    if not is_person:
        members = item_ids(ent, "P527")
        mlabels = {k: label_of(v) for k, v in entities(members).items()}
        known = [m for m in members if mlabels.get(m)]
        if known:
            pick = rng.choice([m for m in known if mention(text, mlabels[m])] or known)
            others = spread(peer_vals["P527"], set(members))
            distractors = [lbl for _, lbl in others if not mention(text, lbl)]
            add(mc(rng, "member", f"Which of these musicians has been a member of {name}?", mlabels[pick], distractors[:15],
                   verified=mention(text, mlabels[pick]), sources=[wd_url] + ([wp_url] if mention(text, mlabels[pick]) else [])),
                "member: not enough comparable musicians")

    # ---- Label
    labels = item_ids(ent, "P264")
    llabels = {k: label_of(v) for k, v in entities(labels).items()}
    in_text = [l for l in labels if llabels.get(l) and mention(text, llabels[l])]
    if labels:
        pick = rng.choice(in_text or [l for l in labels if llabels.get(l)] or [None])
        if pick:
            others = spread(peer_vals["P264"], set(labels))
            distractors = [lbl for _, lbl in others if not mention(text, lbl)]
            add(mc(rng, "label", f"Which record label has released music by {name}?", llabels[pick], distractors[:15],
                   verified=pick in in_text, sources=[wd_url] + ([wp_url] if pick in in_text else [])),
                "label: not enough comparable labels")

    # ---- Debut studio album (MusicBrainz), checked against Wikipedia
    if mbid:
        albums = mb_studio_albums(mbid)
        if len(albums) >= 4:
            debut = albums[0][1]
            if len(albums) > 1 and albums[1][0][:4] == albums[0][0][:4] and len(albums[0][0]) < 10:
                result["skipped"].append("debut album: two albums in the first year with no exact dates")
            else:
                d, t = re.escape(plain(debut)), plain(text)
                verified = bool(re.search(rf"(?:debut|first (?:studio )?album)[^.]{{0,120}}{d}|{d}[^.]{{0,120}}(?:debut|first (?:studio )?album)", t, re.I))
                add(mc(rng, "debut_album", f"Which was {name}'s first studio album?", debut, [t for _, t in albums[1:]],
                       verified=verified, sources=[mb_url] + ([wp_url] if verified else [])),
                    "debut album: not enough albums")

    # ---- Facts: awards (Wikidata) and trivia sentences (Wikipedia)
    awards = claims(ent, "P166")
    alabels = {k: label_of(v) for k, v in entities([c["mainsnak"]["datavalue"]["value"]["id"] for c in awards]).items()}
    years_by_award: dict[str, list[int]] = {}
    for c in awards:
        aid = c["mainsnak"]["datavalue"]["value"]["id"]
        if alabels.get(aid):
            y = qualifier_year(c)
            years_by_award.setdefault(alabels[aid], [])
            if y:
                years_by_award[alabels[aid]].append(y)
    ranked = sorted(years_by_award.items(), key=lambda kv: (not re.search(r"grammy|hall of fame|brit|mercury|oscar|academy award|ivor", kv[0], re.I), -len(kv[1])))
    for award, ys in ranked[:args.awards]:
        when = f" ({', '.join(str(y) for y in sorted(set(ys)))})" if ys else ""
        result["facts"].append({"text": f"{name}: {award}{when}.", "source": wd_url, "license": "CC0"})
    for f in prose_facts(text, args.facts):
        result["facts"].append({"text": f["text"], "section": f["section"], "source": wp_url, "license": "CC BY-SA 4.0"})
    return result


# --------------------------------------------------------------------------------------
# Optional: Claude writes questions from the prose facts
# --------------------------------------------------------------------------------------

LLM_SCHEMA = {
    "type": "object",
    "properties": {
        "questions": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "fact_index": {"type": "integer"},
                    "prompt": {"type": "string"},
                    "options": {"type": "array", "items": {"type": "string"}},
                    "answer_index": {"type": "integer"},
                },
                "required": ["fact_index", "prompt", "options", "answer_index"],
                "additionalProperties": False,
            },
        }
    },
    "required": ["questions"],
    "additionalProperties": False,
}

LLM_PROMPT = """You write trivia questions for a music fan app. Fans of {name} answer one multiple-choice
question a week, so questions should reward real fans: specific, fun, and answerable from the band's story.

Write up to {n} questions from the numbered facts below. Rules:
- Each question must be fully answered by its one fact. Do not use outside knowledge for the answer.
- Exactly 4 options, one correct. Wrong options must be plausible and clearly wrong given the fact
  (same type as the answer: a year for a year, a city for a city, a person for a person).
- The prompt must not give away the answer. Keep it under 140 characters. Use the artist's name, not "they".
- Skip facts that are vague, about chart positions, or would need the fan to know an exact number.
- Reword; do not copy the fact's sentence.

Facts:
{facts}
"""


def llm_questions(result: dict, model: str, n: int) -> None:
    import anthropic  # optional dependency

    facts = result["facts"]
    if not facts:
        return
    client = anthropic.Anthropic()
    listing = "\n".join(f"{i}. {f['text']}" for i, f in enumerate(facts))
    resp = client.beta.messages.create(
        model=model,
        max_tokens=16000,
        betas=["server-side-fallback-2026-07-01"],
        fallbacks="default",
        output_config={"effort": "medium", "format": {"type": "json_schema", "schema": LLM_SCHEMA}},
        messages=[{"role": "user", "content": LLM_PROMPT.format(name=result["name"], n=n, facts=listing)}],
    )
    if resp.stop_reason == "refusal":
        result["skipped"].append("llm: request declined")
        return
    if resp.stop_reason == "max_tokens":
        result["skipped"].append("llm: response cut off")
        return
    text = next(b.text for b in resp.content if b.type == "text")
    for q in json.loads(text)["questions"]:
        opts = q["options"]
        ok = (0 <= q["fact_index"] < len(facts) and len(opts) == 4 and len({o.casefold() for o in opts}) == 4
              and 0 <= q["answer_index"] < 4)
        if not ok:
            result["skipped"].append(f"llm: malformed question dropped ({q.get('prompt', '')[:60]})")
            continue
        fact = facts[q["fact_index"]]
        result["questions"].append({
            "kind": "fact", "prompt": q["prompt"], "options": opts, "answer_index": q["answer_index"],
            "answer": opts[q["answer_index"]], "verified": False, "sources": [fact["source"]],
            "note": f"Written by Claude from: {fact['text']}",
        })


# --------------------------------------------------------------------------------------
# Output
# --------------------------------------------------------------------------------------

def write_table(results: list[dict], out) -> None:
    for r in results:
        if "error" in r:
            out.write(f"\n## {r['query']}: {r['error']}\n")
            continue
        out.write(f"\n## {r['name']} ({r['description']})  {r['wikidata']}\n")
        for q in r["questions"]:
            mark = "verified" if q["verified"] else "check"
            out.write(f"\n[{q['kind']}, {mark}] {q['prompt']}\n")
            for i, o in enumerate(q["options"]):
                out.write(f"   {'*' if i == q['answer_index'] else ' '} {o}\n")
            if q.get("note"):
                out.write(f"   note: {q['note']}\n")
        if r["facts"]:
            out.write("\nFacts:\n")
            for f in r["facts"]:
                out.write(f"  - {f['text']}\n")
        if r["skipped"]:
            out.write("\nSkipped: " + "; ".join(r["skipped"]) + "\n")


def write_csv(results: list[dict], out) -> None:
    w = csv.writer(out)
    w.writerow(["artist", "wikidata", "kind", "prompt", "option_1", "option_2", "option_3", "option_4",
                "answer_index", "answer", "verified", "sources", "note"])
    for r in results:
        for q in r.get("questions", []):
            opts = q["options"] + [""] * (4 - len(q["options"]))
            w.writerow([r["name"], r["wikidata"], q["kind"], q["prompt"], *opts, q["answer_index"], q["answer"],
                        q["verified"], " ".join(q["sources"]), q.get("note", "")])


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("artists", nargs="*", help="artist names, Wikidata IDs (Q...), or spotify:<artist id>")
    ap.add_argument("--file", help="one artist per line (# comments allowed)")
    ap.add_argument("--format", choices=["json", "table", "csv"], default="json")
    ap.add_argument("--out", help="write here instead of stdout")
    ap.add_argument("--facts", type=int, default=8, help="Wikipedia trivia sentences per artist (default 8)")
    ap.add_argument("--awards", type=int, default=3, help="award facts per artist from Wikidata (default 3)")
    ap.add_argument("--verified-only", action="store_true", help="only keep questions two sources agree on")
    ap.add_argument("--seed", type=int, help="fixed seed so option order and picks are repeatable")
    ap.add_argument("--llm", action="store_true", help="have Claude write extra questions from the facts (ANTHROPIC_API_KEY)")
    ap.add_argument("--llm-questions", type=int, default=5, help="max Claude-written questions per artist (default 5)")
    ap.add_argument("--model", default="claude-opus-5-5", help="model for --llm (default claude-opus-5-5)")
    args = ap.parse_args()

    names = list(args.artists)
    if args.file:
        with open(args.file) as f:
            names += [l.strip() for l in f if l.strip() and not l.lstrip().startswith("#")]
    if not names:
        ap.error("give at least one artist (or --file)")

    rng = random.Random(args.seed)
    results = []
    for n in names:
        try:
            r = build(n, args, rng)
        except Exception as ex:  # noqa: BLE001 - one bad artist shouldn't stop the batch
            r = {"query": n, "error": f"{type(ex).__name__}: {ex}"}
        if args.llm and "error" not in r:
            try:
                llm_questions(r, args.model, args.llm_questions)
            except Exception as ex:  # noqa: BLE001
                r["skipped"].append(f"llm failed: {ex}")
        if args.verified_only and "questions" in r:
            r["questions"] = [q for q in r["questions"] if q["verified"]]
        results.append(r)

    out = open(args.out, "w", newline="") if args.out else sys.stdout
    try:
        if args.format == "json":
            json.dump({"generated_at": datetime.now(timezone.utc).isoformat(timespec="seconds"), "artists": results},
                      out, indent=2, ensure_ascii=False)
            out.write("\n")
        elif args.format == "csv":
            write_csv(results, out)
        else:
            write_table(results, out)
    finally:
        if out is not sys.stdout:
            out.close()
    nq = sum(len(r.get("questions", [])) for r in results)
    nv = sum(q["verified"] for r in results for q in r.get("questions", []))
    print(f"{nq} questions ({nv} verified) for {len(results)} artists", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
