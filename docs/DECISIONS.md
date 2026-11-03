# Decisions index

One list of product and technical decisions that are **provisional** (in code, waiting for the owner to confirm or
change), **open** (not decided, nothing in code), or **recently decided**. It is an index: details live in the linked
files, and items are ticked in [`PROJECT_TODO.md`](../PROJECT_TODO.md) (mostly under
[Open decisions](../PROJECT_TODO.md#open-decisions)) and [`docs/OWNER_TODO.md`](OWNER_TODO.md) sections C and D.
When a decision is made, record it there with the date, update the source of truth named here, and move the line.

Values were checked against the latest migration or code that sets them on 2026-11-03. "Where" names the source of
truth, so a change is a one-look job. Rules in `scoring_rules` and `fraud_rules` are data: change them with a new
migration (or `PUT /v1/admin/scoring-rules/{key}` for scoring weights), never by editing an applied one.

## Provisional (in effect, confirm or change)

### Scoring
| Decision | Current value | Since | Where | Affects |
| --- | --- | --- | --- | --- |
| Ledger points vs the 0–100 Spotify score | Ledger awards (journal, proof, trivia) are **added on top**; with 18 signals each worth ≤ 100/18, a few awards can outrank a strong listener | 2026-10-21 | `workers/scoring/README.md`; [Open decisions](../PROJECT_TODO.md#open-decisions) | every fan's raw score, so draw order |
| Ledger point values | ticket 50, merch 20, screenshot 5, concert photo 5, album logged 3 (20/day), relisten 1 (10/day), review 4 (10/day), trivia 0.30 (1/day); seed values never confirmed as a set (concert photo and trivia were decided) | 2026-10-18 | `scoring_rules` (migrations 0001, 0012, 0022) | raw scores |
| Synthetic baseline | `Baseline__MedianScore` 15 (0–100 scale), `Baseline__Sigma` 1, `Baseline__BlendK` 200; stored baselines carry `provisional: true` and follow the current settings | 2026-10-21 | `workers/artist-onboarding/ProvisionalBaseline.cs` (`BaselineOptions` defaults; no overrides in `infrastructure/k8s`) | percentiles while an artist has few fans |
| Co-credited tracks | Count for the **primary (first-credited) artist** only | 2026-10-18 | `workers/spotify-sync/SpotifySignals.cs` (`Reduce`); [ADR 0005](adr/0005-spotify-integration.md) | featured artists' fans |
| Co-credited albums | Go to whichever artist Spotify credits first | 2026-10-21 | `workers/artist-onboarding/README.md` | catalog, album journal, saved-album signals |
| In-app likes count toward the score | Songs and albums liked from the app count like any Spotify save (Spotify can't tell them apart) | 2026-10-31 | [Open decisions](../PROJECT_TODO.md#open-decisions) | score signals; a feed tap is a direct lever |

### Eligibility and fraud
| Decision | Current value | Since | Where | Affects |
| --- | --- | --- | --- | --- |
| Trust score caps (tie-break only) | full at 180 days old, 3 verified proofs, plays in 12 of the last 12 weeks; each takedown in the last year lowers good standing | 2026-10-28 | `fan_trust_parts()` (migration 0039) | tie order in draws |
| Play-pattern thresholds | 24/7: 20+ hours on 3 of 14 days; rapid: 70 % of 100+ plays < 40 s apart in 7 days; looping: 300+ plays/7 days, 90 % on 3 tracks (10+ track artists); spike: 300+ plays, 10x the 8-week average, tickets open or within 14 days | 2026-10-29 | `fraud_rules` (migration 0043) | fans flagged for staff review |
| Device, network, and bot thresholds (six) | shared address: 5+ sign-ups or 5+ registrations for one show per address-day (30 days); Spotify reuse: 2+ other accounts or 3+ Spotify accounts (180 days); headless: 2+ registrations without an eligibility check (30 days); instant: 2+ within 10 min of sign-up; polling: 300+ checks of one show a day | 2026-11-01 | `fraud_rules` (migration 0054); [design](security/bot-and-shared-device-detection.md) | fans flagged for staff review |
| Fraud sweep cadence and cooldown | `fraud.detect` every 6 hours; a cleared rule waits 30 days (`cooldown_days`) | 2026-10-29 | `workers/fraud/DetectFraudJob.cs`; `fraud_rules.params` | review load, time to flag |

Thresholds are listed together in Phase 10 of [`PROJECT_TODO.md`](../PROJECT_TODO.md#phase-10--fan-verification-and-anti-fraud) ("Fraud detection thresholds set provisionally").

### Allocations and tickets
| Decision | Current value | Since | Where | Affects |
| --- | --- | --- | --- | --- |
| +1 details | one seat left: the fan goes alone; a named guest can't be changed (the guest may decline before the draw, 0041); no dropping the +1 after the draw; a +1 can be added after the draw until showtime, first come first served | 2026-10-24 | `run_allocation_selection()`, `add_plus_one()` (migrations 0017, 0041); `services/api/src/features/allocations/README.md` | selected fans and guests |
| Presale reminders | fans with a score for the artist, show in a hub city, once, within 24 hours before the presale opens (or at once if announced later), on by default; pushed too since 0032 (category `presales`); no reminder for public on-sales | 2026-10-26 | `notify_presales(p_lead '24 hours')` (migration 0031); `users.presale_reminders` | every fan with a score |
| Reservations | ticket token delivered 24 hours before the show (per-allocation `delivery_hours_before_show`, locked with the rules); returned seats go to the next not-selected fans in the ranking; returns until showtime; the +1 sees the reservation, not the token | 2026-10-25 | migration 0020 (`allocations.delivery_hours_before_show DEFAULT 24`) | ticket holders |
| Pausing an artist with an open allocation | allowed; registration and the draw then refuse (`artist paused`) | 2026-10-21 | `allocation_selection_blocker()` (migration 0036); allocations README | fans registered for that artist's shows |

### Community, journal, and proof
| Decision | Current value | Since | Where | Affects |
| --- | --- | --- | --- | --- |
| Fan level details | middle level names (Groupie … Platinum); caps: 50 albums (log curve), 5 proofs, 10 trivia right, 10 prompts, best three percentiles; shown to followers only | 2026-10-28 | `fan_activity_parts()` (0039), `fan_level_for()` (0040), `FanLevelEndpoints.Names` | every fan's visible level |
| Weekly prompt | a prompt stays open all week until the next Thursday's; the home screen shows the week's prompt on other days | 2026-10-27 | `prompt_week_day()` (migrations 0034, 0051) | prompt answers |
| Takedown effects | a taken-down photo leaves the feed for good but stays as proof with its points; a taken-down log is hidden from all but its author and loses its points | 2026-10-24 | migration 0014 | reported fans |

### Privacy, security, and retention
| Decision | Current value | Since | Where | Affects |
| --- | --- | --- | --- | --- |
| Play ledger retention | 400 days | 2026-11-01 | `Spotify__PlayHistoryRetention` (`packages/spotify/SpotifyOptions.cs`, no override) | every connected fan |
| Log retention | 30 days (Cloud Logging `_Default`, no sinks), including load-balancer client IPs | 2026-11-01 | GCP default; [DATA_INVENTORY](../services/api/src/features/privacy/DATA_INVENTORY.md) | everyone |
| Database backups | 14 days beta, 30 days prod, plus 7 days point-in-time recovery | 2026-11-01 | `sql_backup_retention_days` (`infrastructure/terraform/envs/*/main.tf`), `sql.tf` | deleted data lingers in backups up to 30 days |
| Push tokens | a token moves to whichever account registers it last ("by design for now") | 2026-10-28 | [security review](security/2026-10-28-api-review.md); Cross-cutting in `PROJECT_TODO.md` | shared phones |

## Open (not decided, nothing in code yet)

### Scoring
| Question | Options known | Blocks |
| --- | --- | --- |
| Live "listening now" check-ins and a Spotify data-export upload as signals | scope `user-read-currently-playing` for release-day/listening-party windows; full streaming history as proof | nothing yet; waits on events and the upload flow |
| Exact Spotify signals under current API access | checked for development mode 2026-10-18 (ADR 0005); extended quota unknown | the score at scale |
| Opt-in public scores on the public profile | today none public (`openapi.yaml`, `getUser`) | nothing |

### Eligibility and fraud
| Question | Options known | Blocks |
| --- | --- | --- |
| The eligibility model: which signals count, weights, minimum account/history age | today only `min_raw_score` (2 saved albums) and fraud holds; no minimum age | Phase 10; `registration_blocker()` |
| Binding one person to one account | phone or ID verification at allocation time, without over-collecting | catching linked accounts on separate phones, networks, and Spotify accounts |

### Allocations and tickets
| Question | Options known | Blocks |
| --- | --- | --- |
| Ticketing partner, model, and API access | white-labelled checkout through ticketer APIs, or direct allocation links; Ticketmaster, AXS, SeatGeek | money moving; Phase 11 primary ticketing; purchase confirmation |

### Community, journal, and proof
| Question | Options known | Blocks |
| --- | --- | --- |
| Receipts in the feed | a never-shared `receipt` proof kind, a staff "not shareable" flag at review, or both | Phase 7 item |
| Blind album rankings | which day (not Tuesday or Thursday), how often, what's shown blind, points, which albums | building it (`apps/mobile/TODO.md`) |
| "This or that" game | the day, what's compared, points, results shown | building it |
| Archive meaning and user-facing archive requirements | from the source spec | nothing built for fans |
| Automatic proof verification thresholds (reject / manual / auto-verify) | none in code; every submission goes to `needs_review` | automated checks (`services/api/src/features/verification/TODO.md`) |

### Privacy and retention
All with a proposal in [DATA_INVENTORY](../services/api/src/features/privacy/DATA_INVENTORY.md) "Retention"; today everything here is kept.

| Question | Proposal | Blocks |
| --- | --- | --- |
| Fraud records (flags, evidence, notes, appeals, eligibility audit) | 2 years after the decision, deleted accounts included | Phase 9 privacy review |
| Closed allocations (standing snapshot, reservations) | clear the score snapshot 1 year after the show; reservations may be a legal/tax record | same |
| Notifications | read after 180 days, all after 1 year | same |
| Reports a deleted fan filed | clear `details` at the purge once resolved | same |
| Staff work records, and whether a staff export includes them | 2 years, not exported | same |
| OCR text (`submissions.extracted`) and the export | clear 30 days after the decision; not exported | same |
| Spotify data when the connection stops working (`reauth_required`) | delete after 180 days without a working connection | same |
| Apple/Google subject id in "Download my data" | left out today | same |
| Outbound link clicks of live fans | unlink after 1 year | same |

### Infrastructure
| Question | Options known | Blocks |
| --- | --- | --- |
| Email provider | a transactional email API | staff email alerts, later fan email |
| Prod domain | prod overlay hostname is a placeholder (`infrastructure/k8s/overlays/prod`, `apps/mobile/eas.json`) | prod setup ([OWNER_TODO](OWNER_TODO.md) E) |
| Repository split | org or personal account, PRs for infra bumps, repo names, stop after step 3 or split | [repo-split plan](plans/2026-10-31-repo-split.md) |

### Business and legal
| Question | Options known | Blocks |
| --- | --- | --- |
| Product and company name | TrueFan Access, VerifyTicket (summary); repo says Fandom Score; beta domain says firstline | store listings, prod domain |
| Pilot artist(s) and promoter | independent mid-size acts and promoters | the pilot |
| Promoter pricing | per-event licence fee or take-rate | Phase 11 billing |
| Fan-side fee, and App Store / Google Play payment rules | fee or none | billing |
| Spotify (and Apple Music) terms for eligibility; extended quota | development mode allows 5 users; extended quota needs 250k MAU | launch |
| Ticketmaster API terms (no revenue from the API, caching) | partner agreement; check SeatGeek, Bandsintown, Songkick | concert listings in a paid product |
| Presale/news scraper sources | newsletters, official sites, RSS; X and Instagram forbid scraping | the scraper |
| Non-transferable ticket and resale laws per launch market | some US states restrict non-transferable tickets | launch markets |

## Recently decided (2026-10-20 to 2026-11-03)

Where recorded is `PROJECT_TODO.md` unless another file is named.

### Scoring
- 2026-10-21: even split across the Spotify signals; 2026-10-24: 18 signals, still even (`scoring_rules`, migrations 0008, 0015).
- 2026-10-21: concert photo points = 5, same as a screenshot (Open decisions).
- 2026-10-21: album catalog from Spotify, metadata cached weekly, art hotlinked ([ADR 0006](adr/0006-album-catalog.md)).
- 2026-10-22: untracked artists in a fan's Spotify top artists are tracked for that fan, under the daily limit (ADR 0005).
- 2026-10-25: trivia right answer = 0.30, a tenth of `album_logged`, once a day (Phase 6).
- 2026-10-25: the app syncs quietly in the background each time it opens (Phase 4).
- 2026-10-30: undated playlist items and follows present at the first sync are baseline and never count as a burst (migration 0049).

### Eligibility and fraud
- 2026-10-24: eligibility threshold = the raw score of 2 saved albums (`allocation_default_threshold()`, migration 0017).
- 2026-10-28: overall trust score, used only to break ties (migration 0039).
- 2026-10-29: fraud review rules: an open flag holds, the fan is told the reason category, one appeal, tickets already given stay, flags kept after deletion (Phase 10).
- 2026-10-29: an open rule flag only blocks registering; the full hold starts when staff uphold it (Phase 10).
- 2026-10-30: automatic flags stay hidden from the fan until staff review (migration 0044).
- 2026-10-30: burst-engagement thresholds confirmed (Phase 10).
- 2026-11-02: `shared_device` fires at 2 accounts on one phone in 90 days, was 3 (migration 0055).
- 2026-11-02: client-signal retention confirmed: address hashes 30 days (salt 2), device history 90, Spotify history 180, eligibility counts 90; not in the data download ([design](security/bot-and-shared-device-detection.md)).
- 2026-11-02: edge bot management deferred to before the first real on-sale (Cloud Armor + reCAPTCHA Enterprise); no log-based cadence alert; no tighter per-address limits ([OWNER_TODO](OWNER_TODO.md) C and D; Phase 12).

### Allocations and tickets
- 2026-10-20: ranked by fandom score, hub fans first, unfilled seats to others, ties by a recorded random draw; a registration keeps its hubs.
- 2026-10-20: a concert hub is a city; three per fan, required at sign-up, changeable once a week with a warning; 2026-10-21: from a curated US/Canada list.
- 2026-10-20: concert data from concert APIs; 2026-10-26: Ticketmaster, SeatGeek, Bandsintown, Songkick, Ticketmaster first.
- 2026-10-24: +1 allowed (takes a seat, guest needs an account, asked at registration or after selection); rules lock at registration open; ranking uses the registration snapshot.
- 2026-10-24: "most likely" shown only inside a 25-seat margin (`Allocations:LikelyMarginSeats`).
- 2026-10-27: promoter organizations created by staff, no self-serve; promoters see handles and status only.
- 2026-10-27: no suburb-to-hub venue mapping (`workers/concerts/README.md`).
- 2026-10-28: a named +1 doesn't have to accept (can decline before the draw) (Cross-cutting).
- 2026-10-30: scalping complaints hand-entered for the pilot; revisit with the ticketing partner (migration 0047).
- 2026-10-30: fans without a hub city count as excluded in the eligible-fan preview (`services/api/src/features/allocations/TODO.md`).

### Community and journal
- 2026-10-20: follow/follower, no approval; grab bag of 5 songs, shared with followers, can't be turned off; bouncer door song is a real track.
- 2026-10-21: only verified merch and concert photos can be shared; concert photos pinned to a show.
- 2026-10-24: blocking not wanted for now.
- 2026-10-25: comments replaced by emoji reactions; stats followers-only, none public by default; featured artist picked by staff.
- 2026-10-27: ordered album lists not wanted; trivia Tuesdays and prompts Thursdays (UTC); a trivia question stays open until answered; blind rankings never on a trivia or prompt day.
- 2026-10-28: fan level from activity, ten named levels, never decides tickets (migrations 0039, 0040); others' prompt answers may be seen before answering.
- 2026-10-31: beta runs trivia and prompts daily for demos, prod stays weekly (`app_settings.engagement_cadence`, migration 0051).

### Privacy and retention
- 2026-10-25: files of rejected submissions and taken-down photos kept 30 days; quarantine 30 days; unused upload slots 1 day.

### Infrastructure
- 2026-10-24: beta and prod on GCP; dev runs locally.
- 2026-10-29: first iPhone test is a free-Apple-ID build on a Mac (`apps/mobile/TODO.md`).
- 2026-10-31: beta domain `firstline-5730kl.com` through Cloud Domains and Cloud DNS ([OWNER_TODO](OWNER_TODO.md) A1).
