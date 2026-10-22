# Fandom Score — Architecture Checklist and Overview

## Intended boundaries
- **Mobile app:** user experience, local/offline journal queue, native sharing, push notification handling.
- **API:** authentication, validation, authorization, domain operations, and persistence coordination.
- **PostgreSQL:** durable user, artist, signal, journal, submission, ledger, and score data.
- **Background workers:** Spotify sync, artist onboarding, score computation, verification, redaction/archive, notifications.
- **Redis:** cache/fast score lookup and/or job infrastructure only if selected by the project.
- **Object storage:** quarantined originals and redacted archive files.
- **External integrations:** Spotify (listening signals and album catalog), ticketing partner, OCR/vision, native sharing.

## Architectural rules
- Input services persist normalized signals; scoring consumes stored signals and ledger events.
- Only tracked artists may be synced, scored, journaled, or verified.
- Adding an artist is shared across users; onboarding must finish catalog/baseline setup before activation.
- Event-based rewards flow through one append-only points ledger; reverse awards rather than deleting ledger history.
- Scoring weights live in database configuration, not hard-coded constants.
- Background work must not block API requests.
- Sensitive uploads stay quarantined until checks complete; archive only after redaction.
- Enforce authorization on the server, not only in the client.

## Decisions to confirm
- Backend language/framework and job queue.
- Album metadata provider and permitted artwork/cache behavior. **Decided:** Spotify, see [ADR 0006](docs/adr/0006-album-catalog.md).
- Ticketing partner and purchase-confirmation capabilities.
- Archive's exact product meaning.
- Verification thresholds and baseline blend parameters.
- Data retention and account deletion policy.
- Handling of collaborations/co-credited albums.
