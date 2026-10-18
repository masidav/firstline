# TODO — Primary ticketing integration

Use `- [x]` only after the task is implemented and verified. Adapt this checklist to existing code; do not duplicate working functionality.

## Implementation checklist
- [ ] Confirm which ticketers (Ticketmaster, AXS, SeatGeek) offer API access for reserved inventory, and their contract terms.
- [ ] Choose the checkout model: white-labeled checkout through the ticketer, or direct allocation links.
- [ ] Implement seat or inventory hold, release, and purchase confirmation for the first partner only.
- [ ] Verify webhook signatures; make confirmations idempotent; reconcile holds against confirmed sales.
- [ ] Pass identity binding to the ticketer where supported so tickets stay non-transferable.
- [ ] Add sandbox-based integration tests and a manual verification checklist.

## Verification
- [ ] Add or update automated tests for the behavior above.
- [ ] Run the relevant tests and record actual results.
- [ ] Update the feature README with setup, interfaces, dependencies, and known limitations.
- [ ] Update `PROJECT_TODO.md` when this feature reaches a verified milestone.
