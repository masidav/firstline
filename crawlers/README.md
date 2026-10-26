# crawlers

Personal web-crawling scripts. Not part of the product build; nothing here is deployed.

```bash
cd crawlers
pip install -r requirements.txt
python3 music_industry_moves.py                        # hires/exits in the last 7 days, as a table
python3 music_industry_moves.py --days 14 --format csv --out moves.csv
python3 music_industry_moves.py --state seen.json      # repeat runs only report new stories
python3 music_industry_moves.py --llm                  # Claude extracts name/company/title (ANTHROPIC_API_KEY)
```

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
