# Fandom Score: what the company and the product are

*Last generated 2026-10-24 (first version, written by hand). Refreshed weekly by the product documentation
job; the instructions it follows are in `PROMPTS.md` in this folder.*

## In one paragraph

Fandom Score (working name) is a ticketing platform that puts real fans first. Promoters and artists set
aside a share of each show's tickets for an artist's verified top fans, who buy them at face value before the
public sale and cannot resell them. Fans prove their fandom through what they already do: listening,
keeping an album journal, and going to shows. The company sells this as a service to promoters and artists,
and offers fans an app where their loyalty finally counts for something.

## The problem

When tickets for a popular show go on sale, scalpers and automated bots buy the best seats in seconds and
relist them at several times the price. The artist sees none of that money. The promoter gets the complaints.
And the people who have followed the band for years either pay the markup or stay home. Ticketing is also
concentrated in a few large companies, so neither artists nor fans have much say in how it works.

## What we believe

Scalpers should not make money from fans. Ticketing should not be controlled by a handful of companies. Live
music should be as accessible as a public good: if you love an artist, you should be able to see them play at
the price they set.

## Who it is for

**Fans.** People who stream an artist constantly, buy the records and the shirts, and show up. Today they are
routinely beaten by bots. Fandom Score gives them a fair, early chance at a face-value ticket and a place to
keep their listening life.

**Artists.** Touring acts from club level to arenas who want their real fans in the room and want to protect
them from exploitative pricing. They get a view of who their top fans are and a guarantee that the seats they
set aside go to those people.

**Promoters.** Independent and major concert promoters who want fewer scalping scandals, better sell-through,
and first-party knowledge of their audience. They choose how many seats to set aside and on what terms, and
get an auditable record of who got each one.

## How it works

**For a fan.** You open the app and sign in with Apple or Google. You pick the artists you care about and
connect your Spotify account so the app can see how you listen to them. Over time you log the albums you
listen to, rate them, write reviews, and upload photos of ticket stubs and merch from past shows, which our
team checks. All of this builds your fandom score for each artist. When one of your artists announces a show
with a fan allocation, you register during the registration window and say whether you want to bring a +1.
The app tells you where you stand. When registration closes, a draw ranks fans from the show's city first,
then by score, with ties broken by a recorded coin flip. If you are selected you get a face-value,
non-transferable ticket. (The last step, turning a selection into a delivered ticket, is not built yet.)

**For a promoter or artist.** You create the show and decide how many seats to set aside, at what face value,
how strong a fan needs to be to qualify, and when registration opens and closes. Once registration opens the
rules are locked, so nobody can move the goalposts. After it closes you run the draw and get the full ranked
list and the random seed, so anyone can check the result. Today our staff do this on a promoter's behalf; a
self-service promoter dashboard is planned.

## What exists today

The product is a working prototype, run by the team for testing. It is not yet launched to the public or
hosted for real users.

A fan can, today:
- sign in with Apple or Google and choose a handle;
- follow artists and connect Spotify, after which the app measures their listening for those artists;
- see their fandom score and standing for each artist;
- keep an album journal: log albums, rate them, write reviews, and see what the community thinks;
- upload proof of fandom (ticket stubs, merch, concert photos) for review, and share approved merch and
  concert photos to a home feed;
- set their three hub cities;
- see an artist's upcoming shows with fan allocations, register for one, ask for a +1, and see where they
  stand.

Staff can, today:
- create shows and allocations, publish and cancel them, and run the draw;
- review proof-of-fandom uploads and approve or reject them;
- adjust how much each listening signal counts.

Not built yet: the ticket itself. Nothing is reserved with a ticketing company, and no ticket is delivered
after the draw.

## What is next

In roughly the order the team plans to work on it:

- **Reservations and delivery.** Turn a selection into a held seat and a non-transferable ticket, with a
  face-value way out for fans who cannot attend.
- **Ticketing partner integrations.** Connect to the companies that actually sell tickets so reserved seats
  can be checked out there.
- **Promoter dashboard.** Let promoters and artists set up allocations, watch registration, and export
  reports themselves.
- **Anti-fraud.** Detect streaming farms and bot accounts, tie one person to one account at allocation time,
  add an appeals process, and stress-test the scoring rules before each pilot.
- **More ways to prove fandom.** Apple Music, purchases from an artist's own store, verified attendance.
- **Hosting.** Put the service on cloud infrastructure for real users (the setup is written but not yet
  switched on), then test the app end to end and run the first pilot.
- **The rest of the fan app.** Ordered album lists, comments, following other fans, notifications.

## How the company makes money

From the business plan; none of this is in operation yet.

- **Promoters and ticketing partners** pay a flat software fee per event or a small percentage of tickets
  processed through the platform.
- **Fans** may pay a small, clearly stated service fee in exchange for guaranteed access with no resale
  markup. Whether to charge fans at all is still an open decision.

## Go-to-market

1. **Pilot.** Partner with independent mid-sized touring acts (venues of roughly 2,000 to 10,000) and
   independent promoters who control their own seat allocations.
2. **Prove it.** Show higher sell-through, no empty seats, no scalping complaints, and real first-party data
   for the artist.
3. **Scale.** Integrate with the major primary ticketing companies and pitch booking agencies and
   management firms directly.

## Open questions

- Which ticketing companies will let us reserve inventory through their systems, and on what terms.
- Whether fans check out through a ticketing partner's own pages or through direct allocation links.
- Whether Spotify's and Apple's developer terms allow listening data to decide ticket eligibility, and
  whether the app can get the data access it needs at scale.
- Laws on non-transferable tickets and resale in each market where we launch; some US states restrict
  non-transferable tickets.
- How to stop people faking fandom with streaming farms or shared accounts, without collecting more
  personal data than necessary.
- Whether fans pay a fee.
- The company and product name: the business plan suggests "TrueFan Access" or "VerifyTicket"; the project
  is called Fandom Score.

## About this report

This report is generated every Monday by an automated job that reads the repository and rewrites this file,
the business-logic document beside it, and the plain-words summaries at the top of each folder's README. The
instructions it follows are in `docs/product/PROMPTS.md`; edit that file to change what gets written. This
version was written by hand on 2026-10-24 as the starting point.
