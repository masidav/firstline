# TODO — Database and migrations

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [x] Inspect and preserve the existing schema.sql and migration history. (Now `migrations/0001_initial_schema.sql`.)
- [ ] Verify the schema against PostgreSQL and document the supported PostgreSQL version. (Verified on 16.15; 14 untested.)
- [x] Create a repeatable migration workflow; do not rely on manually edited production schemas. (DbUp + `migrations/`, tested for re-run.)
- [ ] Ensure required extensions, foreign keys, indexes, checks, and triggers are included in migrations.
- [x] Verify tracked-artist uniqueness, active-artist baseline guard, and onboarding status constraints. (`tests/invariants.sql`)
- [x] Verify the append-only score ledger and unique award-source constraints. (`tests/invariants.sql`)
- [ ] Verify daily reward caps and concurrency protection in award_points().
- [ ] Verify submission status constraints, verified_at requirements, reviewer requirements, and archive redaction guard.
- [ ] Seed scoring_rules with documented initial values without hard-coding weights in application code.
- [ ] Add reproducible development seed data that contains no real personal data or credentials.
- [ ] Add database tests for functions, triggers, reversals, duplicate prevention, and paused artists.
- [ ] Document backup, restore, migration rollback, and data-retention considerations.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
