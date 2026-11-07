# The API contract this app is built against

`openapi.yaml` here is a copy of the backend's contract at the version in `VERSION`; `src/api/schema.d.ts` is
generated from it (`npm run generate:api`). Don't edit either by hand: `npm run sync:api` refreshes both from
`../../openapi.yaml` while this app lives in the backend's repository, and `npm run sync:api -- <version>` from
the backend's release `contract-v<version>` once it doesn't. CI checks that what is committed matches.
Rules and plan: `openapi.yaml` ("Contract version") and `docs/plans/2026-10-31-repo-split.md` in the backend.
