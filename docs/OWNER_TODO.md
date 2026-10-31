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

- [ ] **6. Turn deploys on and deploy beta.** After this, every push to `main` deploys beta. All three certificates
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

- [ ] **7. Sign up, then make yourself staff.** Open https://app.beta.firstline-5730kl.com, choose Continue with Google,
  and pick a handle. Then:
  ```bash
  infrastructure/k8s/db-shell.sh beta <<'SQL'
  UPDATE users SET is_staff = true WHERE email = 'YOUR_EMAIL';
  SQL
  ```
  Staff see the review, moderation, featured, prompts, and concert-source screens in the fan app.
  `db-shell.sh` opens psql on the beta database from a temporary pod in the cluster. It is new (2026-10-28) and
  hasn't been run against the cluster yet; if it fails, send Claude the output.

- [ ] **8. Create a promoter organization** with you as owner, then try the dashboard at
  https://promoters.beta.firstline-5730kl.com.
  ```bash
  infrastructure/k8s/db-shell.sh beta <<'SQL'
  WITH me AS (SELECT id FROM users WHERE email = 'YOUR_EMAIL'),
       org AS (INSERT INTO organizations (name, created_by) SELECT 'Test Promoter', id FROM me RETURNING id)
  INSERT INTO organization_members (organization_id, user_id, role, added_by)
  SELECT org.id, me.id, 'owner', me.id FROM org, me;
  SQL
  ```

## B. Features that need a provider account

- [ ] **Spotify** (listening score, artist search, album catalog, Spotify connect). Off on beta today.
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
- [ ] **Email** for staff alerts now and fan email later. Needs a provider chosen (section C).

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

Still open:
- [ ] Receipts in the feed: a separate never-shared "receipt" proof kind, a staff "not shareable" flag, or both.
- [ ] Blind album rankings: Tuesday or Thursday.
- [ ] How much journal and proof points weigh against the 0 to 100 Spotify score.
- [ ] Verification thresholds and baseline parameters (provisional defaults in code).
- [ ] How co-credited tracks and albums count.
- [ ] Email provider.
- [ ] Product and company name.
- [ ] "Listening now" check-ins and a Spotify data-export upload as extra signals.

## D. Business and legal, before a real launch

- [ ] Choose the pilot artist(s) and promoter.
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
