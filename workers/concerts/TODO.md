# TODO — Concert data

Use `- [x]` only after the task is implemented and verified.

- [x] Ticketmaster Discovery: link artists by their Spotify page, load upcoming US and Canadian shows with status, ticket
      link, public sale, and presales; keep allocation-locked times; staff link and unlink. Verified 2026-10-26:
      ConcertSyncJobTests 7, ConcertApiTests 2, invariants for 0030.
- [ ] Try it with a real Ticketmaster API key against real artists (needs `Ticketmaster__ApiKey`); check how many
      attractions list Spotify links and tune matching if most don't.
- [ ] SeatGeek, Bandsintown, and Songkick as further sources (decided 2026-10-26: all four). One show listed by
      several sources should become one event: match by date and venue.
- [ ] Hub cities for suburb venues (Rosemont, Inglewood): give cities coordinates and use the nearest within a radius.
- [ ] Tell staff when a show with an allocation is rescheduled at the source (`source_starts_at` differs).
- [ ] Staff screen in the app for linking artists (today: the API only).
- [ ] Presale reminders as notifications (once push notifications exist).
