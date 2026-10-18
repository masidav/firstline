# TODO — External integrations

Use `- [x]` only after the task is implemented and verified.

## Implementation checklist
- [ ] Define provider interfaces only where they reduce real coupling or support a confirmed provider choice.
- [ ] Keep credentials in secure configuration and never log secrets or raw sensitive payloads.
- [ ] Add timeouts, rate-limit handling, bounded retries, and normalized error mapping.
- [ ] Document each provider's terms, scopes, data retention, caching, and attribution constraints.
- [ ] Use mocked tests for normal behavior, throttling, outages, and malformed responses.
- [ ] Verify current provider capabilities before depending on them.

## Verification
- [ ] Add or update automated tests.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the corresponding README and root project checklist.
