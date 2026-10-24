# How Fandom Score works: the rules in plain English

*Last generated 2026-10-24 (first version, written by hand). Refreshed weekly by the product documentation
job; the instructions it follows are in `PROMPTS.md` in this folder.*

Fandom Score (working name) is a fan-first ticketing platform. Promoters and artists set aside a share of a
show's tickets for verified top fans, at face value, non-transferable, before the public sale. This document
explains the rules: who counts as a top fan, how seats are handed out, and what keeps it fair.

## The promise

**To fans.** If you are genuinely one of an artist's biggest fans, you get a fair shot at a face-value ticket
before bots and scalpers do. You never pay a resale markup. You prove your fandom with things you already do:
listening, keeping an album journal, going to shows.

**To artists.** Your most loyal fans end up in the room, not resellers. You see who your top fans are, and the
tickets you set aside cannot be flipped.

**To promoters.** A share of every show sells to real people at the price you set, with a clear, auditable
record of who got each seat and why. Fewer scalping complaints, fewer empty seats, and first-party knowledge
of the audience.

## Who counts as a top fan: the fandom score

Every fan has a score for each artist they follow in the app. The score is per artist: being a huge fan of one
band says nothing about another.

**What counts today**

- **Listening on Spotify.** A fan connects their Spotify account. The app looks at how much and how long they
  have listened to the artist: whether the artist is among their top artists over the last month, six months,
  and all time; saved albums and how early they saved them; tracks they have saved or put on playlists;
  whether they follow the artist; how often they have played the artist recently and how many weeks in a row;
  how much of the catalogue they know, including the deep cuts. Eighteen such signals, each counting equally,
  make up a listening score from 0 to 100.
- **The album journal.** Fans log the albums they listen to, rate them, and write reviews, like a diary for
  music. Logging an album, listening again, and writing a real review each earn points.
- **Proof of fandom.** Fans upload photos of ticket stubs, merchandise, or concert screenshots. A staff member
  reviews each one. Points are awarded only after the proof is approved, and taken back if it is later
  rejected.

**What does not count**

- Listening to artists the fan does not follow in the app. That data is never stored.
- Anything a fan says about themselves without evidence.
- Buying a ticket on a resale site.

**Where a fan stands.** For each artist, the app also shows a percentile: roughly, "you are in the top 5% of
this artist's fans here". When an artist is new to the app and has few fans, the percentile is an estimate and
is labelled as such.

**Still being decided.** How much the journal and proof points should weigh against the listening score, and
whether to add Apple Music, purchases from an artist's own store, or a full listening-history export as further
evidence.

## How a show's allocation works

A promoter or artist (today, our staff on their behalf) creates a **show** and an **allocation**: a pool of
seats for that show at face value, with:

- a **registration window**, which closes before the show;
- an **eligibility threshold**: the minimum listening score a fan needs for that artist. If the promoter sets
  none, the default is about what a fan who has saved two of the artist's albums would have.

Fans see the allocation in the app and **register** during the window. To register, a fan must follow the
artist, have a score for them, meet the threshold, and have set their **hub cities**: up to three cities a fan
calls home or travels to for shows. A fan registers once per show and can withdraw while the window is open.

**The +1.** A registering fan can ask for a second seat for a guest. The guest needs their own account, which
can come later, and cannot be registered for the same show themselves. A selected fan can still add a +1 after
the draw if seats remain.

**What is locked.** Once registration opens, the allocation's rules cannot change. Publishing, cancelling,
and the draw cannot be undone. A fan's registration keeps the hub cities and score they had when they
registered, so later changes do not reshuffle a draw.

## How fans are selected when seats run out

After registration closes, staff run the **draw**. It follows fixed, published steps:

1. **Hub fans first.** Fans whose hub cities include the show's city are ranked ahead of everyone else.
2. **By fandom score.** Within each group, fans are ranked by their score for the artist as it was when they
   registered.
3. **Ties by coin flip.** Equal scores are separated by a random draw. The random seed is recorded, so anyone
   can re-run the draw and get the same result.
4. **Seats go down the list.** Each selected fan takes one seat, or two with a +1, until the pool is used up.
   Seats hub fans leave unfilled go to fans from outside the city.

Before the draw, the app tells each registered fan where they stand and says "most likely" only when they are
comfortably inside the number of seats (a margin of 25). The draw itself is recorded and auditable.

**Not built yet:** turning a selection into an actual ticket. Reservations, non-transferable delivery, and
the hand-off to the ticketing company's checkout are the next pieces of work.

## What keeps it fair and hard to game

- **Tickets are non-transferable.** The point of the whole product; the specific mechanism (reservation
  tokens, delayed delivery, or a face-value exchange for fans who cannot attend) is still to be built.
- **One account per person.** One email address per account; signing in with a second provider using the same
  email is refused. Stronger binding of a person to an account at allocation time (for example a phone check)
  is planned.
- **Points can be taken back, never quietly edited.** Every point ever awarded is kept in a permanent record.
  When a review is deleted or a proof is rejected, a matching reversal is added. Nothing is erased, so every
  score can be explained.
- **Proof is reviewed by people.** Today every upload goes to a staff queue. Automated checks (duplicate
  photos, reading the ticket text, matching it to a real event) are planned.
- **Scores are treated as an attack surface.** Because the score now decides who gets tickets, the team's
  stated rule is that every signal is something a scalper will try to fake. Detecting streaming farms and bot
  accounts, red-teaming the rules before each pilot, and an appeals process are on the roadmap but not built.
- **The draw is reproducible.** See above.

## Privacy and data

- **Only followed artists.** Listening data is stored only for artists a fan has chosen to follow in the app.
- **Deleting an account.** Personal details are scrubbed immediately. Journal entries, photos, and other
  content are permanently deleted after 30 days. Replies other fans wrote to a deleted comment stay, marked
  "[deleted]". The anonymous points record is kept so past draws still add up.
- **Uploaded photos.** Ticket and merch photos are held in a quarantine area, stripped of hidden metadata
  (location, device), and are only kept long-term after barcodes and QR codes have been blacked out. Ticket
  photos and screenshots are never shown to other fans; fans may choose to share approved merch and concert
  photos to the feed.
- **Data for artists and promoters.** Sharing first-party fan data with them is planned only with each fan's
  explicit consent.

## Money

From the company's plan (not yet in operation):

- **Promoters** pay either a flat software fee per event or a small percentage of tickets handled through the
  platform.
- **Fans** may pay a small, transparent service fee for guaranteed access. Whether to charge fans at all is
  still open. There is no markup on the ticket price.

## Decided versus still open

| Rule | Status |
| --- | --- |
| Tickets for top fans are face value and non-transferable, sold before the public sale | Decided (product direction, 2026-10-18) |
| Fandom score is per artist and uses only followed artists' data | Decided |
| Eighteen Spotify listening signals, each counting equally, make the 0–100 listening score | Decided 2026-10-21 and 2026-10-24 |
| Journal and proof points are added on top of the listening score | Provisional; weighting is open |
| One email per account; no account linking | Decided 2026-10-18 |
| Fans have up to three hub cities; hub fans are selected first | Decided 2026-10-20 |
| Selection is ranked by score; ties broken by a recorded random draw | Decided 2026-10-20 |
| Unfilled hub seats go to fans from elsewhere | Decided 2026-10-20 |
| A selected fan may bring a +1; allocation rules lock once registration opens | Decided 2026-10-24 |
| "Most likely" shown only inside a 25-seat margin | Decided 2026-10-24 |
| Account deletion: scrub at once, content gone after 30 days, anonymous ledger kept | Decided 2026-10-18 |
| How tickets are actually reserved and delivered | Open |
| Which ticketing companies will give access to reserved inventory | Open |
| White-labelled checkout versus direct allocation links | Open |
| Whether Spotify's and Apple's terms allow using listening data for ticket eligibility | Open |
| Laws on non-transferable tickets in each launch market | Open |
| Whether fans pay a service fee | Open |
| The company and product name | Open |
