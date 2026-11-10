# TODO — Bot and fake-account detection on the home feed

Use `- [x]` only after the task is implemented and verified; record what was actually run.

Written 2026-11-10 from the three papers in `docs/supplemental/` and a read of the feed, follows, reactions,
reports, and fraud code. Nothing here is built yet. Items marked **(decision)** need the owner before any code.

## Sources
- **[K]** Kozlov et al. (Facebook), *Evaluating Changes to Fake Account Verification Systems*. The PDF in the
  folder is cut off after page 3 and has no fonts; the abstract, introduction, and related work were recovered,
  the method and results were not. Its numbers did not survive reliably and are not quoted here.
- **[B]** Behfar, Shrestha, Al-Ameen, *A First Look into Fake Profiles on Social Media through the Lens of Victim's
  Experiences* (CSCW Companion 2024): interviews with 26 people deceived by fake profiles.
- **[S]** Sarayu et al., *Fake Social Media Profile Detection and Reporting* (IJSREM 2025): a short survey.

What they say that matters here:
- Fakes come in three kinds: automated, hand-run, and mixed. Detection tuned to automation misses the hand-run
  ones **[K]**. The existing rules (`docs/security/bot-and-shared-device-detection.md`) are all of the first kind.
- Four ways to detect: behaviour (rate limits, anomalies, accounts acting in step), the follow graph (fakes make
  many accounts but few real fans connect to them), the profile's footprint (name, picture, description,
  address), and trained models (need labelled data) **[K][S]**.
- Respond with friction, not removal: a wrongly flagged real fan must be able to get back in, and the friction
  can grow with confidence. Measure each change, and watch for attackers adapting afterwards **[K]**.
- What victims later recognised: a generic or screenshot picture, a name that isn't a name, under 10 followers,
  no recent posts, no reactions from others, filler praise repeated by other fake accounts, several profiles
  with the same story **[B]**.
- How victims were found: follower lists of public pages, public comments, friends' lists **[B]**.
- What victims wanted from the platform: warnings and examples, new profiles watched for a while, a check on
  sudden follower jumps, and an answer when they report, fast and in plain words **[B]**.

## What the feed does today
- Shared photos, public album logs, and public prompt answers from **every** account reach **every** fan
  (`FeedEndpoints.cs`, `CandidatesSql`). Photos are staff-verified first; logs and answers are free text and are not.
- Each source contributes its newest 100 items with no limit per author.
- The feed never looks at `fraud_flags`: a held fan still posts to everyone.
- Account age, a connected music service, and the trust score play no part in what reaches the feed.
- Reports cover posts only (`photo`, `log`, `prompt_answer`), 30 a day per reporter. A person can't be reported.
  The reporter never hears the outcome.
- Follow and unfollow are rate-limited (`AbuseLimits.Churn`). Reactions and posting have only the general limiter.
- Handles and display names are checked for shape only. Nothing stops "taylorswift_official" or "firstline_support".
- Already safe: nobody can list another fan's followers or following (only `/me/following`, `/me/followers`), and
  stats are followers-only, so the main route victims were found by in **[B]** is closed. Keep it closed.
- Sign-in is Apple or Google only, one email per account, which already raises the cost of bulk sign-ups.

## 1. Close the cheap gaps first
- [ ] **Cap items per author in each feed pool.** Today one account that posts 100 logs fills the whole album-log
      pool for everyone. Take the newest N per author (start with 3) before the pool limit. Test in `FeedApiTests`.
- [ ] **Leave held fans out of other fans' feeds.** Under the full hold (`fan_full_hold_flag()`), their photos,
      logs, answers, and reactions don't reach others; they still see their own, so nothing reveals the hold.
      **(decision)** whether an open rule flag, which is hidden from the fan until reviewed, does the same.
- [ ] **Limit reactions and posting per fan.** Put reactions under `AbuseLimits.Churn`; add a daily ceiling on
      public logs that reach the feed (the log is still saved, it just stays off the shared feed past the ceiling).
- [ ] **Reserved and lookalike names.** At sign-up and `PATCH /me`, refuse handles and display names that contain
      staff or brand words ("official", "support", "firstline", "staff", "presale") or match a tracked artist's
      name, unless the account is that artist (`is_artist`) or staff. Compare after folding case, accents, and
      lookalike characters. New error code in `openapi.yaml`.
- [ ] **Add the feed to the red-team list.** New strategies in `docs/security/gaming-strategies.md`: flooding the
      pool, sockpuppet reactions and follows, impersonating an artist or staff, ticket-resale bait, report brigades.

## 2. New accounts start with less reach **[B][K]**
- [ ] **Probation for the shared feed (decision).** Until an account has earned some standing, its public logs and
      answers reach its followers only. Standing should reuse the trust score parts that already exist (account
      age, verified proof, steady listening, good standing) rather than a new score. The owner picks the bar.
      A proposal to start from: a connected music service with a score for at least one artist, and 7 days old.
      Thresholds live in a table, like `fraud_rules.params`.
- [ ] **Count follows from established fans.** A fake cluster follows itself; real fans rarely follow it **[K]**.
      Record for staff, per account, how many followers are themselves past probation. Use it as evidence and
      as one way out of probation, never as the only gate.
- [ ] **Off-platform handles (decision).** `instagram_handle` and `x_handle` are on every fan page. They are the
      obvious way to pull a fan off the app to a scam. Proposal: show them only for accounts past probation, or
      only to followers.

## 3. Report a person, and answer the reporter **[B]**
- [ ] **Report an account.** `target_type = 'user'` from the fan page, with reasons `impersonation`, `scam`
      (new: selling tickets, asking for money or codes), `fake_account`, `other`. Same one-open-report and
      daily limits as posts. Migration, invariants, `openapi.yaml`, `ReportApiTests`.
- [ ] **Staff view of a reported account.** Beside the reports: account age, connected services, scores, follower
      counts and how many are established, posts taken down, open fraud flags, and the profile as others see it.
- [ ] **An account-level outcome.** Staff can limit an account to its followers or hold it, recorded in
      `moderation_actions`, reversible, with the appeal path the fraud flags already have. **(decision)** on the
      outcomes offered; nothing automatic.
- [ ] **Tell the reporter what happened.** A notice when staff decide ("we removed it" / "we looked and left it
      up"), in plain words. Silence and confusing replies were the strongest complaint in **[B]**.
- [ ] **Scam and impersonation reports go first** in the staff queue, with a staff email (`StaffEmailJob`, migration 0059).
- [ ] **Ask what happened.** The report form's free text prompts for the story when the reason is scam or
      impersonation. Staff use these to keep the fan-facing warning page (section 6) current.
- [ ] **Guard the report button.** Count how often each reporter's reports are upheld; show it to staff. Flag a
      group of linked accounts (the 0054 device and address-day links) reporting the same author. Reports never
      take anything down on their own.

## 4. Automatic detection: new rules in the `fraud.detect` sweep
Each follows the existing pattern: a row in `fraud_rules` with thresholds as data, evidence as counts only, a flag
for staff and nothing automatic, an invariant check, and a `FraudReviewApiTests` case that fires at the threshold
and not below it. A new flag reason is needed (`fake_profile` or `spam`) since these aren't streaming fraud.
**(decision)** what an open rule flag of this kind blocks: registering (as today), feed reach, or both.

Behaviour **[K][S]**
- [ ] `repeated_text`: the same or near-same review or answer posted many times by one account, or by several
      accounts. Compare a hash of the text with case, spacing, and punctuation removed. This is the filler praise
      and copied stories from **[B]**.
- [ ] `contact_bait`: a review, caption, answer, or display name carrying a link, a phone number, a payment
      handle, or resale wording ("selling", "DM me", "extra ticket", "presale code"). The post stays off the
      shared feed until staff look. Tickets here can't be transferred, so every such offer is false.
- [ ] `acting_in_step`: sets of accounts that react to or follow the same targets inside the same short windows,
      repeatedly (the SynchroTrap idea cited in **[K]**). Reactions and follows already carry times.
- [ ] `follower_surge`: an account gaining followers far above its usual rate in a day, mostly from accounts
      still in probation **[B]**.
- [ ] `follow_spray`: a new account following a large number of fans in its first days.
- [ ] `reaction_ring`: most reactions on an author's posts come from a few accounts that share a device or
      address-day with the author.

Footprint **[K][B]**
- [ ] `shared_avatar`: the same picture file on several accounts (a byte hash, as `file_hash` does for proof
      photos since 0061). A cropped copy has new bytes; note that as a known gap.
- [ ] `lookalike_profile`: a handle or display name one edit away from a much-followed fan or a tracked artist,
      for names the reserved list didn't refuse.
- [ ] Thin-profile evidence: no picture, no connected service, no followers, nothing but posts. Shown to staff
      with any flag; never a rule on its own, since a quiet new fan looks the same.

Privacy for all of these: counts and hashes only, a fixed retention, deleted with the account, listed in
`DATA_INVENTORY.md`, covered by `AccountDeletionTests` (the 0054 rules).

## 5. Friction before removal **[K]**
- [ ] **A ladder of responses**, chosen by how sure the evidence is: nothing; followers-only reach; a check the
      fan can pass to restore reach; a staff hold. Write it down in `docs/security/` before building any step.
- [ ] **The check itself (decision).** Options from **[K]**: phone confirmation, a CAPTCHA on posting, email
      confirmation. Phone confirmation is the same work as the open "one person per account" item in the fraud
      TODO; build it once. reCAPTCHA Enterprise is already planned at the edge before the first on-sale.
- [ ] **Keep what the check shows.** Whether and how fast it was passed is itself a signal **[K]**; store the
      result and a duration, nothing else.
- [ ] **Never say which rule fired** (already the rule for fraud flags).

## 6. Help fans spot fakes themselves **[B]**
- [ ] **One plain statement, shown where it matters:** fan tickets can't be sold or transferred, FirstLine staff
      never message fans asking for money or codes, so anyone offering either is a scam. On the report form, the
      fan page of an account in probation, and a help page under Settings.
- [ ] **Context on the fan page:** when the account joined, and "followed by N fans you follow". Mutual
      connections are what people actually use to judge a stranger **[B]**.
- [ ] **A reply for a fan who was scammed:** a staff template and a route in the support plan
      (`docs/plans/2026-11-07-ai-support-todo.md`).

## 7. Measure, then tune **[K][S]**
- [ ] **Precision per rule.** Every staff decision is a label. A staff view of flags upheld against flags cleared
      for each rule, so thresholds are changed from results rather than guesses.
- [ ] **Shadow mode for rules.** A rule can run and record what it would have flagged without raising anything,
      so a new rule or threshold is tried on real traffic first.
- [ ] **Look again later.** After a rule or check ships, re-run the numbers a few weeks on; a fall in what it
      catches usually means the attackers adapted **[K]**.

## Not recommended now
- A trained classifier **[S][K]**: there are no labelled examples and no real users yet. Revisit once section 7
  has a few hundred staff decisions.
- Reverse image search on profile pictures **[B][S]**: sends fans' photos to a third party.
- Government ID at sign-up **[B]**: heavy for a beta, and the fraud TODO already carries the lighter phone option.
- Blockchain identity **[S]**: no fit.
- Public follower lists or "people you may know": the papers show these are how targets are found.

## Order
Section 1 needs no decisions and no new data; do it first. Sections 2 and 3 need three owner decisions (the
probation bar, off-platform handles, account-level outcomes). Section 4 depends on the new flag reason and is
worth little until there are real users to measure against; `repeated_text`, `contact_bait`, and `shared_avatar`
are the cheapest. Sections 5 to 7 follow the first pilot.
