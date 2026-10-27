# TODO — Concert data

Use `- [x]` only after the task is implemented and verified.

- [x] Ticketmaster Discovery: link artists by their Spotify page, load upcoming US and Canadian shows with status, ticket
      link, public sale, and presales; keep allocation-locked times; staff link and unlink. Verified 2026-10-26:
      ConcertSyncJobTests 7, ConcertApiTests 2, invariants for 0030.
- [ ] Try it with a real Ticketmaster API key against real artists (needs `Ticketmaster__ApiKey`); check how many
      attractions list Spotify links and tune matching if most don't.
- [ ] SeatGeek, Bandsintown, and Songkick as further sources (decided 2026-10-26: all four). One show listed by
      several sources should become one event: match by date and venue.
- [x] Tell staff when a show with an allocation is moved, cancelled, postponed, or rescheduled at the source
      (`staff_show_changed`, migration 0036; once per change). Verified 2026-10-27: ConcertSyncJobTests, invariants.
- [x] Staff screen in the app for linking artists: Profile → Concert sources (`/concert-sources`). Verified: Jest.
- [x] Presale reminders as in-app notifications (migration 0031, `workers/notifications/PresaleReminderJob.cs`).
      Verified 2026-10-26: NotificationApiTests, invariants. They reach phones once push notifications exist.
