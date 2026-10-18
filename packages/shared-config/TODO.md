# TODO — Shared configuration

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [ ] Identify lint, formatting, TypeScript, or test configuration that should be shared.
- [ ] Keep secrets and environment-specific settings out of shared configuration.
- [ ] Reuse only configuration supported by the actual project toolchain.
- [ ] Document how each app/service consumes the shared configuration.
- [ ] Verify all project checks still run after changes.

## Verification
- [ ] Add or update automated tests.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the corresponding README and root project checklist.
