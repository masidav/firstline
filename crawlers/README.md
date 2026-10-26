# crawlers

Personal web-crawling scripts. Not part of the product build; nothing here is deployed.

```bash
cd crawlers
pip install -r requirements.txt
python3 music_industry_moves.py                        # hires/exits in the last 7 days, as a table
python3 music_industry_moves.py --days 14 --format csv --out moves.csv
python3 music_industry_moves.py --state seen.json      # repeat runs only report new stories
python3 music_industry_moves.py --llm                  # Claude extracts name/company/title (ANTHROPIC_API_KEY)

python3 band_trivia.py Paramore Radiohead --format table     # trivia questions and facts
python3 band_trivia.py --file artists.txt --format csv --out trivia.csv
```

Both scripts identify themselves to the sites they call. Set `CRAWLER_CONTACT` (an email or URL) to add
contact details to the User-Agent; Wikimedia asks for one if you run these often.

## music_industry_moves.py

Finds hires, promotions, departures and layoffs at labels, publishers, promoters, agencies,
streaming services and the rest of the music business, and extracts **name, company, position**
and the kind of move (`hired`, `promoted`, `departed`, `laid_off`, `other`).

### Where it looks

| Source | How |
|---|---|
| Billboard *Executive Turntable* (weekly column) and *Layoffs* tag | RSS; roundup pages are opened and split into one entry per person |
| Music Business Worldwide, Variety (music), The Hollywood Reporter (music), Pollstar, MusicRow, Complete Music Update, Music Ally, Digital Music News | RSS |
| Music Week, Hits Daily Double, Record of the Day, Puck | Google News search (these have no usable feed or are paywalled; headlines are still indexed) |
| Anything else | Google News searches for label/publisher/promoter names and move verbs |

Why not LinkedIn: LinkedIn blocks scraping (login wall, bot detection, and it is against their
terms), and it only shows a change after the person updates their profile. The trade press above
reports the same moves on the day they are announced, usually with the title and who they report to.

### How extraction works

Rule-based by default: headlines in this beat follow a few shapes
("*Company* Names *Person* *Title*", "*Person* Named *Title* at *Company*",
"*Person* Exits *Company*", "*Company* Lays Off ..."), and the parser handles those plus
roundups ("Promotes A and B to ...", "A Exits; B Named ..."). The headline and URL are always
included so anything odd can be checked. Headlines that match the move words but cannot be
parsed are dropped unless you pass `--all`.

`--llm` sends each headline and first paragraph to Claude with a fixed JSON schema and uses
its answer instead. It is noticeably better on roundups and unusual phrasing. Default model is
`claude-opus-5-5`; override with `--model`. Needs `ANTHROPIC_API_KEY` (or `ant auth login`).

### Options

```
--days N        look back N days (default 7; also sets the Google News window)
--format        table (default) | csv | json
--out FILE      write there instead of stdout
--state FILE    remember seen URLs; later runs only report new stories
--only a,b      keep only sources whose name contains one of these substrings
--no-google     skip the Google News searches
--no-expand     don't open Billboard roundup pages
--all           include matched stories that couldn't be parsed
--llm / --model use Claude for extraction
```

### Notes

- Google News links are redirect URLs, not the article's own URL.
- Some feeds return 403 or time out now and then (Digital Music News, Music Ally); the run
  reports it on stderr and continues.
- To run it on a schedule, `--state` plus `--format csv --out` is the intended combination.

## band_trivia.py

Finds trivia about bands and solo artists and turns it into multiple-choice questions in the same
shape the app's trivia uses (`kind`, `prompt`, `options`, `answer_index`), plus a list of trivia
facts for a person to turn into questions.

```bash
python3 band_trivia.py Paramore "Fleetwood Mac"              # JSON on stdout
python3 band_trivia.py spotify:74XFHRwlV6OrjEM0A2NCMF         # exact match by Spotify artist ID
python3 band_trivia.py Q44190 --format table                  # by Wikidata ID
python3 band_trivia.py --file artists.txt --verified-only --format csv --out trivia.csv
python3 band_trivia.py Paramore --llm                         # Claude also writes questions from the facts
```

### Where the trivia comes from

| Source | License | Used for |
|---|---|---|
| Wikidata | CC0 | formation year and place, members, labels, birth year and place, awards |
| MusicBrainz | CC0 (core data) | studio albums with release dates; begin date and area as a cross-check |
| Wikipedia | CC BY-SA 4.0 | sentences that read like trivia ("named after", "originally called", "met at", "the only band to") |

Questions built from Wikidata and MusicBrainz are free to use. Wikipedia sentences under `facts` are
CC BY-SA: reword them before publishing (the `--llm` prompt does), or credit Wikipedia.

Songfacts, Genius, and fan wikis have more song-level trivia, but their write-ups are copyrighted editorial
content and their terms don't allow reuse, so the script doesn't scrape them.

### Question kinds

| kind | Example |
|---|---|
| `formed_year` / `birth_year` | In what year did Fleetwood Mac form? |
| `formed_place` / `birthplace` | Where did Paramore form? |
| `member` | Which of these musicians has been a member of Paramore? |
| `label` | Which record label has released music by Paramore? |
| `debut_album` | Which was Paramore's first studio album? |
| `fact` | written by Claude from a Wikipedia fact (`--llm` only) |

The app's trivia API only knows `track_album` and `release_year` today. Using these kinds there means
adding them to `openapi.yaml` and the `trivia_questions.kind` check in a new migration.

### How it keeps answers right

- **Verified** means two independent sources agree on the answer: Wikidata and MusicBrainz on a year,
  Wikidata and the Wikipedia article on a member, label or place, MusicBrainz and Wikipedia on the
  debut album. `--verified-only` drops the rest.
- When sources disagree (Paramore: Wikidata says 2003, MusicBrainz and Wikipedia say 2004) the
  majority wins and the question carries a note. With no majority it is skipped and listed under `skipped`.
- Wrong options come from comparable artists: the best-known artists sharing a genre and country.
  Any option that appears anywhere in the artist's Wikipedia article is dropped, so a "wrong" member
  or label can't secretly be right. Staff should still read every question before it ships.

### Options

```
artists...           names, Wikidata IDs (Q...), or spotify:<artist id>
--file FILE          one artist per line (# comments allowed)
--format             json (default) | table | csv
--out FILE           write there instead of stdout
--facts N            Wikipedia trivia sentences per artist (default 8)
--awards N           award facts per artist (default 3)
--verified-only      keep only questions two sources agree on
--seed N             repeatable option order and picks
--llm                Claude writes up to --llm-questions more questions from the facts
--model              model for --llm (default claude-opus-5-5)
```

It takes roughly a minute per artist, mostly waiting on the Wikidata query service and MusicBrainz's
one-request-per-second limit.
