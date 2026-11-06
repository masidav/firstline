# Owner to-do

Things only you can do: accounts, DNS, console settings, secrets, and decisions. Written 2026-10-28; Claude keeps it
current. Tick items as you go.

State on 2026-10-28: beta's cloud resources exist and the API was deployed once (2026-10-26), but deploys have been
off since 2026-10-27, no DNS records exist, and no certificate is issued. The beta secrets `Auth__SigningKey` and
`Spotify__TokenKey` hold real values; `Spotify__ClientSecret`, `Ticketmaster__ApiKey`, and
`Auth__Apple__PrivateKey` are placeholders (those features are off).

## A. Turn on everything already built (beta)

Do these in order, from the repo root unless a step says otherwise. At the end beta serves the API, the fan app at
https://app.beta.firstline-5730kl.com, and the promoter dashboard at https://promoters.beta.firstline-5730kl.com, with Google
sign-in. Spotify, concerts, and Apple need provider accounts first (section B).

- [x] **1. Domain.** `firstline-5730kl.com`, registered through Cloud Domains on 2026-10-31 (renews 2027-10-31 on
  the project's billing). Cloud Domains created its Cloud DNS zone `firstline-5730kl-com` and pointed the registrar
  at it, and Terraform manages the beta records in that zone (`dns_zone_name` in `envs/beta`), so no outside DNS
  provider is involved. firstline.com was ruled out: it belongs to FirstLine Mortgages and its DNS is at Akamai.
  To switch domains later, the hostnames are in Terraform (`envs/beta/variables.tf`, `terraform.tfvars`), the beta
  overlay (`gateway.yaml`, `web-config`, `Cors__AllowedOrigins`), `.github/workflows/mobile-ios.yml`,
  `apps/mobile/eas.json`, and the docs; grep for the old domain.

- [x] **2. Sign in to Google Cloud.** Done 2026-10-31.
  ```bash
  gcloud auth login --update-adc
  gcloud config set project project-ef36a8a9-d958-4163-b4c
  gcloud container clusters get-credentials fandomscore-beta --region us-central1
  ```

- [x] **3. Apply Terraform on beta.** Applied 2026-10-31: 11 added, 5 changed, 5 destroyed, as planned (the two web
  certificates, and the redaction service account taking over bucket and secret access from the worker per commit
  d75d4af). Terraform 1.17.1 and kubectl 1.37.2 live in `~/.local/bin`; `terraform.tfvars` is filled in (gitignored).
  A re-plan still shows one cosmetic in-place change to the monitoring dashboard: the API adds default
  `targetAxis`/`xPos`/`yPos` values the repo's JSON omits. Harmless; Claude can add the defaults to the JSON later.
  ```bash
  cd infrastructure/terraform/envs/beta
  terraform plan        # re-run after any change under infrastructure/terraform
  terraform apply
  ```

- [x] **4. Apply the domain change and wait for the certificates.** Applied 2026-10-31 (17 added, 2 changed, 1
  destroyed; the first attempt failed until the uptime check became create-before-destroy, commit 540e9e5). The six
  records exist in Cloud DNS and all three hostnames resolve to `136.81.191.137` publicly. The certificates were
  `PROVISIONING`/`AUTHORIZING` right after the apply and turn `ACTIVE` on their own, usually within an hour:
  ```bash
  gcloud certificate-manager certificates list --format='table(name.basename(), managed.domains.list(), managed.state)'
  ```

- [x] **5. Google sign-in.** Done 2026-10-31. Cloud console, APIs & Services, Credentials, the web client
  `494880048811-14ch6rdq...`:
  - Authorized JavaScript origins: `https://app.beta.firstline-5730kl.com` and `https://promoters.beta.firstline-5730kl.com`.
  - Authorized redirect URIs: `https://app.beta.firstline-5730kl.com/google-callback` and
    `https://promoters.beta.firstline-5730kl.com/google-callback`.
  - OAuth consent screen: while it's in Testing, add each tester's Google account as a test user (up to 100).

- [x] **6. Turn deploys on and deploy beta.** Done 2026-10-31: `DEPLOY_ENABLED=true`, beta deployed (the first run
  rolled out fine but failed its one-minute health check while the recreated Gateway was still provisioning; commit
  a438523 waits up to 10 minutes, and the next push deployed green). About 12 minutes after the Gateway appeared,
  the API, the fan app, and the promoter dashboard all answered over HTTPS with Google-issued certificates.
  Every push to `main` now deploys beta. Since 2026-10-31 the deploy also writes demo data once (200 fake fans, 8
  made-up artists, 10 shows; `infrastructure/README.md`, "Demo data"), so the app isn't empty when you show it, and
  beta runs trivia and prompts daily instead of weekly (`overlays/beta/settings`, "Environment settings"). The demo
  account `secondfan` (d57590971@gmail.com) is pre-provisioned with a full feed: sign in with Google as that address
  and it's yours, no handle step. Add that Google account as an OAuth test user first (step 5).
  - [ ] **Real artists in the demo data** (requested 2026-10-31). Two steps, in order:
    1. `cd infrastructure/terraform/envs/beta && terraform apply` (1 to add: the deploy identity may read
       `Spotify__ClientSecret`; plus the usual cosmetic dashboard change).
    2. `infrastructure/k8s/db-wipe.sh beta`, then `gh workflow run Deploy --repo masidav/firstline -f environment=beta`.
       The wipe empties beta (your accounts too: sign in again afterwards; `secondfan` is set up again by email). The
       deploy's "Demo data" step should then say "artists: real, from Spotify".
  To pause beta while nobody is testing (it costs money idle):
  `infrastructure/k8s/set-state.sh beta off`; `... beta on` brings it back in about 15 minutes
  (`infrastructure/README.md`, "Turning an environment off and on"). All three certificates
  were `ACTIVE` by 2026-10-31 evening, but beta was torn down on 2026-10-27 (api and worker scaled to 0, Gateway and
  load balancer gone), so `https://` on the hostnames fails until this deploy recreates the Gateway.
  ```bash
  gh variable set DEPLOY_ENABLED --body true --repo masidav/firstline
  gh workflow run Deploy --repo masidav/firstline -f environment=beta   # add -f skip_health_check=true if a certificate isn't ACTIVE yet
  ```
  Check:
  ```bash
  curl https://api.beta.firstline-5730kl.com/v1/health
  curl https://app.beta.firstline-5730kl.com/config.js
  ```

- [x] **7. Sign up, then make yourself staff.** Done 2026-11-05: `db-shell.sh beta` set `is_staff` on
  d57590971@gmail.com (the `secondfan` demo account, now the owner's account; `UPDATE 1`), first real run of
  `db-shell.sh` against the cluster. Open https://app.beta.firstline-5730kl.com, choose Continue with Google,
  and pick a handle. Then:
  ```bash
  infrastructure/k8s/db-shell.sh beta <<'SQL'
  UPDATE users SET is_staff = true WHERE email = 'd57590971@gmail.com';
  SQL
  ```
  Staff see the review, moderation, featured, prompts, and concert-source screens in the fan app.
  `db-shell.sh` opens psql on the beta database from a temporary pod in the cluster. It is new (2026-10-28) and
  hasn't been run against the cluster yet; if it fails, send Claude the output.

- [x] **8. Create a promoter organization** (done 2026-11-05: 'Test Promoter', owner d57590971@gmail.com) with you as owner, then try the dashboard at
  https://promoters.beta.firstline-5730kl.com.
  ```bash
  infrastructure/k8s/db-shell.sh beta <<'SQL'
  WITH me AS (SELECT id FROM users WHERE email = 'd57590971@gmail.com'),
       org AS (INSERT INTO organizations (name, created_by) SELECT 'Test Promoter', id FROM me RETURNING id)
  INSERT INTO organization_members (organization_id, user_id, role, added_by)
  SELECT org.id, me.id, 'owner', me.id FROM org, me;
  SQL
  ```

- [x] **9. Apply Terraform for the archive bucket's old versions** (applied 2026-11-05) (added 2026-11-01, commit 32ce75f). The archive
  bucket is versioned, so redacted proof photos deleted by fans or staff lived on as old versions forever; the new
  lifecycle rule deletes them 30 days after they are replaced or deleted. From `infrastructure/terraform/envs/beta`:
  `terraform plan` should show only an in-place change to the archive bucket (plus the dashboard's usual cosmetic
  diff), then `terraform apply`.

- [x] **10. Run the beta on-sale load test** Done 2026-11-05 by Claude (results in `tools/load/README.md`,
  "Results on beta"): 25 fans/s passed (p95 30 ms), 50 fans/s failed with the `db-g1-small` database at 100 % CPU
  (connection timeouts, no double registrations), the Cloud Armor pass passed; test data cleaned up, HPA back to 2.
  To run it again (after a database tier change, before a real on-sale, or whenever Claude asks for a re-measure),
  outside testers' hours and announced, one command from the repo root:
  ```bash
  tools/load/beta.sh --dry-run   # prints every command it would run, runs none
  tools/load/beta.sh             # the real thing, about 25 minutes
  ```
  It checks gcloud, kubectl (the beta cluster), gh, and jq, reads the HPA and the database (nobody's hub is a test
  city, `max_connections`), then asks once, `Run this on beta now? [y/N]`. Answer `y` and it pre-scales the API to
  6 pods, runs the 25 and 50 fans/s passes in the cluster and the 1 fan/s pass through Cloud Armor, checks the
  registrations, cleans up every tag, and puts the HPA back. Paste back everything between
  `----- paste from here -----` and `----- to here -----` (also in `tools/load/results/beta-<date>.txt`), with each
  pass's `DB CPU peak: ____` filled in from the dashboard's Cloud SQL CPU tile for the window the pass names;
  Claude records it in `tools/load/README.md`. If it stops partway it cleans up itself and prints the manual
  commands if that fails. The steps it runs are the numbered list in `tools/load/README.md`, "Running it on beta".

- [ ] **11. Add the `ANTHROPIC_API_KEY` repository secret** (or delete the workflow). Deferred 2026-11-05: keep the
  workflow, add the secret later; until then it fails each Monday. `.github/workflows/weekly-docs.yml`
  ("Weekly product docs", Mondays 09:00 UTC) rewrites the product docs with Claude and commits the result; it has
  failed every week since 2026-10-26 because the secret is missing (run 41507776960: "Either ANTHROPIC_API_KEY ...
  is required"). GitHub → Settings → Secrets and variables → Actions → New repository secret. Billed to your
  Anthropic account per run.

## B. Features that need a provider account

- [x] **Spotify** (listening score, artist search, album catalog, Spotify connect). On beta since 2026-10-31: app
  created, client ID `e2d216438a484d2299cfdc6608ac41e7` in the overlay, secret stored (it had a trailing newline;
  the API now trims it). Still yours: add each tester's Spotify account under User Management.
  1. developer.spotify.com, Dashboard: create an app for beta (Web API), with the redirect URI
     `https://app.beta.firstline-5730kl.com/spotify-callback`.
  2. User Management: add each tester's Spotify account (development mode allows 5).
  3. Store the client secret without it landing in your shell history:
     ```bash
     read -rs SECRET && printf '%s' "$SECRET" | gcloud secrets versions add Spotify__ClientSecret --data-file=- && unset SECRET
     ```
  4. Send Claude the client ID (it isn't secret). Claude sets `Spotify__ClientId`, `Spotify__RedirectUris`, and the
     fan app's `SPOTIFY_CLIENT_ID` in the beta overlay and pushes, which deploys.
- [ ] **Ticketmaster** (concert listings, sale dates, presale reminders). Get a key at developer.ticketmaster.com
  and store it the same way as `Ticketmaster__ApiKey`, then restart the worker so it reads the new value:
  `kubectl -n fandomscore rollout restart deployment/worker`. Check the terms question in section D first.
- [ ] **Apple sign-in, the iPhone app, and push notifications** (when you want iOS). The app is configured and its
  iOS project is checked in CI (bundle ID `com.firstline.fandomscore`); the build runs in Expo's cloud, so no Mac is
  needed. You need: the Apple Developer Program ($99/year) and an Expo account (expo.dev, free tier builds work).
  Then, in `apps/mobile`: `npm i -g eas-cli && eas login && eas init` (commit the `app.json` change), and
  `eas credentials -p ios` to let Expo create the certificates and the push key. For sign-in: a Sign in with Apple
  key (the `.p8` goes in `Auth__Apple__PrivateKey`; Team ID and Key ID go in the overlay) and a Google OAuth client of
  type iOS for the bundle ID (send Claude its ID). First build: `eas build -p ios --profile development`. Steps with
  the exact commands: `apps/mobile/README.md`, "Building the iOS app".
  *Before the program (decided 2026-10-29):* on a Mac with Xcode and a free Apple ID, `npm run ios:personal` installs
  the app on your own iPhone for 7 days at a time, without Apple sign-in or push (same README, "On your own iPhone
  with a free Apple ID").
- [ ] **Apple sign-in on the web** (built 2026-11-05; needs the Apple Developer Program). In the developer account:
  a Services ID (e.g. `com.firstline.fandomscore.web`) with Sign in with Apple enabled, domain
  `api.beta.firstline-5730kl.com` verified, and return URL `https://api.beta.firstline-5730kl.com/v1/auth/apple/callback`.
  Then add the Services ID to `Auth__Apple__ClientIds` (overlay, after the bundle ID) and set `APPLE_SERVICES_ID` in
  the beta overlay's `web-config`. The app's side: `apps/mobile/README.md`, "Sign in with Apple".
- [ ] **Apple Music** (built 2026-11-05, ADR 0007; not yet tried against the real API). In the developer account
  (Keys): a key with the **MusicKit** (Media Services) service. Team ID and Key ID go in the overlay
  (`MusicProviders__AppleMusic__TeamId`, `__KeyId`); the `.p8` goes to Secret Manager as
  `MusicProviders__AppleMusic__PrivateKey` and to `secrets.yaml`; set `APPLE_MUSIC_ENABLED=true` in `web-config`.
  Check Apple's MusicKit terms on using listening data for eligibility (section D). The web app connects through
  MusicKit JS; the iPhone app can't yet (no MusicKit module in Expo SDK 57).
- [ ] **Tidal** (built 2026-11-05, ADR 0007; not yet tried against the real API). At developer.tidal.com create an
  app, note the client ID (no secret needed: PKCE), and register the redirect URIs
  `https://app.beta.firstline-5730kl.com/tidal-callback` and `fandomscore://tidal-callback`. Set
  `MusicProviders__Tidal__ClientId` and `__RedirectUris` in the overlay and `TIDAL_CLIENT_ID` in `web-config`. Then
  connect one real account on beta and check the sync (the collection endpoints moved in autumn 2026; ADR 0007 lists
  what to verify).
- [ ] **Email** for staff alerts now and fan email later. Needs a provider chosen (section C). The staff side is
  built (2026-11-06): staff notices queue an email to each staff member's account address, and each staff member has
  a Profile switch for it. Nothing is sent until a provider exists: once you pick one, Claude writes its sender
  class (`workers/notifications/README.md`, "Email"); you store its key like `Ticketmaster__ApiKey`, and the overlay
  gets `Email__Enabled=true`, `Email__Provider`, and `Email__From` (a sending address on a domain you own; the
  provider will ask you to verify the domain with DNS records, which Claude can add to Cloud DNS).

## C. Product decisions

Rules already in code provisionally; confirm or change each (details in `PROJECT_TODO.md`, "Open decisions"):
- [ ] +1 rules: one seat left means the fan goes alone; a named guest can't change; a +1 can't be dropped after the
  draw but can be added until the show starts.
- [ ] Presale reminders: fans with a score for the artist, in a hub city, once, within 24 hours before opening,
  on by default.
- [ ] Reservations: tickets delivered 24 hours before the show; returned seats go to the next fans in the ranking;
  returns allowed until the show starts.
- [ ] Takedowns: a taken-down photo stays as proof with its points; a taken-down log loses its points.
- [ ] Pausing an artist with an open allocation: for now registration and selection refuse.
- [ ] Location checks (2026-11-05, `docs/security/hub-location-checks.md`): a hub counts as matched within 100 km;
  `hub_location_mismatch` at 2 far days (500 km) in 90 days; `impossible_travel` at 1 500 km within 6 hours; checks
  kept 90 days and out of the fan's data download; `hub_confirmed` weighs like the other four trust parts.
- [ ] Listening from two services (2026-11-05, ADR 0007): the score takes the strongest signal per artist across
  services, never the sum; Apple Music can't be one-account-per-fan (no account id).

Still open:
- [ ] Which of the proposed verification signals to build next, and their weights
  (`docs/product/fandom-verification-signals.md`; suggested: release-day listening, journal-backed logs, human
  listening shape, attendance).
- [ ] Receipts in the feed: a separate never-shared "receipt" proof kind, a staff "not shareable" flag, or both.
- [ ] Blind album rankings: which day and how often (not Tuesday or Thursday, decided 2026-10-27; those are trivia and prompt days).
- [ ] How much journal and proof points weigh against the 0 to 100 Spotify score.
- [ ] Verification thresholds and baseline parameters (provisional defaults in code).
- [ ] How co-credited tracks and albums count.
- [ ] Email provider.
- [ ] Product and company name.
- [ ] "Listening now" check-ins and a Spotify data-export upload as extra signals.
- [x] Bot protection at the edge (found missing 2026-11-01; WAF and rate limiting are live). *Decided 2026-11-02:
  nothing more at the edge for now; the API-side rules (migration 0054) and staff review cover beta. Before the first
  real on-sale, add Cloud Armor bot management with reCAPTCHA Enterprise (section D). A log-based cadence alert was
  rejected (the API's daily polling rule already covers it) and tighter per-address rate limits were rejected (they
  punish campuses and carrier NATs). Options compared in `docs/security/bot-and-shared-device-detection.md`.*

## D. Business and legal, before a real launch

- [ ] Choose the pilot artist(s) and promoter.
- [ ] Edge bot management before the first real on-sale (decided 2026-11-02): Cloud Armor bot management with
  reCAPTCHA Enterprise. Needs a reCAPTCHA Enterprise key in the beta and prod projects, the web app loading the
  reCAPTCHA script and the phone app embedding the SDK, a privacy-notice line for reCAPTCHA, the Cloud Armor rule in
  Terraform, and a rehearsal with the on-sale load test (`tools/load/README.md`) before it goes live. Billed per
  assessment above the free monthly tier. Size it against pilot timing; Claude can build it when you say so.
- [ ] Ticketing partner and model (checkout through the ticketer, or direct allocation links), and which ticketers
  will give API access to reserved inventory.
- [ ] Spotify's developer terms: confirm listening data may decide ticket eligibility, and how to get past
  development mode's 5-user limit (extended quota is for organizations with 250,000 monthly users).
- [ ] Ticketmaster's API terms forbid revenue from the API: check whether a partner agreement is needed.
- [ ] Laws on non-transferable tickets and resale in each launch market.
- [ ] Fan-side fee, and whether App Store and Google Play payment rules apply.
- [ ] Which presale and news sources a scraper may use under their terms.

## E. Prod, once beta is solid

- [ ] Create the prod project and follow `infrastructure/README.md`, "Set up a cloud environment": Terraform, DNS,
  secrets, a prod domain, the GitHub `prod` environment with required reviewers, and `PROD_DEPLOY_ENABLED`.
- [ ] Rehearse a Cloud SQL point-in-time restore on beta and record the result.
