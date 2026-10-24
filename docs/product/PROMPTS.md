# Prompts for the weekly product documentation job

Edit this file to change what the automation writes. Nothing else needs to change: the workflow
(`.github/workflows/weekly-docs.yml`) hands this whole file to Claude every Monday and asks it to follow the
section for each task. Edits take effect on the next run (or run the workflow by hand from the Actions tab).

Each `##` section below is one task. Keep the headings as they are; the workflow refers to them by name.
Write the prompts as instructions to a careful writer who can read the whole repository.

---

## Voice (applies to every task)

- The reader is a non-technical person: an investor, a promoter, an artist's manager, a new teammate who does
  not code. Write for them.
- Describe what the app and the company do and why, not how the software works inside. No code, no file
  names, no database or API terms, no acronyms without a plain-English explanation. If a technical detail is
  the only way to explain a rule, say it in one plain sentence.
- Short sentences. Everyday words. Concrete examples ("a fan who has streamed the band for two years") beat
  abstractions ("high-affinity users").
- Say what is true today. If something is planned but not built, say "planned" or "not yet". Take the state of
  the work from `PROJECT_TODO.md` (a `[x]` means done; `[ ]` means not done) and from the feature `TODO.md`
  files, not from wishful reading of the code.
- Never invent numbers, partners, customers, or launch dates. Open decisions are listed in `PROJECT_TODO.md`
  under "Open decisions"; present them as open.
- The company and product name is still an open decision. Use "Fandom Score (working name)" the first time and
  "Fandom Score" after that.
- Source material, in order of authority: `executive_summary.txt` (what the company is), `company_ethos.txt`
  (what it believes), `PROJECT_TODO.md` (what exists and what is next), then the feature READMEs and TODOs.

---

## Task: Folder summaries

Goal: every folder README in the repository opens with a short plain-words explanation of what that part of
the product is for, so a non-technical reader can browse the codebase and understand it.

Which READMEs: every `README.md` in the repository **except** the root `README.md`, anything under
`docs/product/`, and anything under `node_modules/` or other generated folders.

What to write in each one, directly under the first heading:

```
<!-- product-summary:start -->
> **In plain words:** two to four sentences. What this part of the product does for fans, artists, or
> promoters, and why it exists. If it is not built yet, say so. Written for a non-technical reader.
<!-- product-summary:end -->
```

Rules:
- If the block already exists, rewrite only the text between the markers. Leave everything else in the
  README alone: the developer content below the block belongs to the engineers.
- If the README is still the generic starter template (it says "This folder owns the ... responsibilities" and
  "This README is a starter document"), replace the template body with a short, accurate description of what
  the folder actually contains, in addition to the plain-words block. Read the folder's files and its `TODO.md`
  first. Do not describe things that are not there.
- Only revisit folders that changed since the last run, plus any README that has no block yet or is still the
  starter template. Find changed folders with `git log --since="8 days ago" --name-only`.
- Do not create new README files; only update ones that exist.

---

## Task: Business logic

Goal: one document, `docs/product/BUSINESS_LOGIC.md`, that explains the rules of the product in plain
English: who counts as a top fan, how tickets are set aside and handed out, what keeps it fair, and what the
company promises about privacy and money. A promoter or an artist's manager should be able to read it in ten
minutes and know exactly how the product would treat their fans.

Cover, in this order:
1. The promise to fans, artists, and promoters, in one short paragraph each.
2. How the fandom score works: what counts (Spotify listening, the album journal, proof of fandom such as
   ticket stubs and merch photos), what does not count, that it is per artist, and that only artists a fan
   follows in the app are measured.
3. How a show's allocation works: who can register, the registration window, the eligibility threshold, the
   +1 guest, and what "hub cities" mean.
4. How fans are selected when more register than there are seats, step by step, including the recorded
   random tie-break and the fact that the draw can be audited.
5. What keeps it fair and hard to game: non-transferable tickets, one account per person, points that are
   reversed when something turns out to be false, staff review of proof, and the stance that every scoring
   signal is treated as something a scalper will try to exploit.
6. Privacy and data: what is stored, what is deleted when an account is deleted, and how uploaded photos
   are handled.
7. Money: how the company plans to earn revenue and what fans do or do not pay.
8. Decided versus still open: a short table of the main rules, each marked "decided" (with the date from
   `PROJECT_TODO.md` when there is one) or "open".

Rules:
- Rewrite the whole file each run from the current state of the repository; keep its heading structure.
- Start the file with a one-line note: when it was last generated and that it is maintained by this job.
- Do not include code, SQL, endpoint paths, migration numbers, or file names.

---

## Task: Product description

Goal: one report, `docs/product/PRODUCT_DESCRIPTION.md`, that describes the company and the product for
someone who has never heard of it: what problem it solves, for whom, what the product is, how far along it
is, how it makes money, and where it is going. This is the document to send to a non-technical person who
asks "so what is this?".

Synthesize it from: `executive_summary.txt`, `company_ethos.txt`, `docs/product/BUSINESS_LOGIC.md`, the
plain-words blocks at the top of the folder READMEs, and the phase list in `PROJECT_TODO.md`.

Structure:
1. **In one paragraph** — the whole company in five sentences or fewer.
2. **The problem** — scalpers, bots, and fans priced out; why artists and promoters lose too.
3. **What we believe** — the company ethos in respectful, publishable language (the source file is blunt;
   keep its meaning, not its wording).
4. **Who it is for** — fans, artists, promoters: one short paragraph each, with what each gets.
5. **How it works** — the fan's experience from first opening the app to holding a ticket, and the
   promoter's experience from setting aside seats to seeing who got them. Plain words, no technology.
6. **What exists today** — what a fan can do in the app right now, and what staff can do. Be honest about
   what is a working prototype versus launched.
7. **What is next** — the upcoming work in plain terms (ticket reservations and delivery, ticketing partner
   integrations, the promoter dashboard, anti-fraud, Apple Music, hosting, the first pilot), taken from the
   unchecked items in `PROJECT_TODO.md`.
8. **How the company makes money** — from the executive summary; mark anything not yet decided.
9. **Go-to-market** — the pilot plan from the executive summary.
10. **Open questions** — the open decisions a reader should know about, in plain words.
11. **About this report** — generated weekly by this job; where the prompts live; the date.

Rules:
- Rewrite the whole file each run; keep the heading structure above.
- Two to four pages when printed. No code, no file names except `docs/product/PROMPTS.md` in the last section.
- No marketing superlatives. State facts and intentions plainly.
