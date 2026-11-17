# CalCrony Game Nights extension: Phase 1 build plan

Read this file. Work through it one step at a time, starting at the first unchecked step. Stop after each step, report, and wait for "next".

Design reference: the "CalCrony Game Nights, Tournaments and Leagues Extension" doc (https://claude.ai/artifact/Db1K2KH5F2VLtBmFpQvvyW). If this plan and the doc disagree, ask.

## 0. How this plan maps onto our fork (read first)

The plan was written for a generic fork. Ours already has a pipeline and branch model (see `HOME.md`), so these overrides apply and win over the wording below:

- **Branches.** There is no `main` or `feature/game-nights` long-lived branch. Each step is a short-lived branch (`feat/game-nights-step-N`) off `dev`, merged to `dev` by **pull request** (squash), after `build-test` and `pr-source` pass. Merging to `dev` deploys to the **test** stack on the Pi. Nothing in this plan goes to `prod` until the owner promotes `dev` → `prod`.
- **Upstream remote** is already configured (`upstream` = JJWren/CalCrony); `master` mirrors it.
- **Notes file.** The plan's `docs/FORK-NOTES.md` is created as specified (it holds recon answers and per-step notes). `HOME.md` stays the place for the branch/pipeline model and the customisations list; list the Game Nights touch points in both.
- **Commit message** format `step N: <summary>` applies to the commit on the branch; the squash-merge title to `dev` should be the same.
- **No local .NET SDK.** CI is the compiler and test runner. "Build passes, tests pass" means the PR checks are green.
- **Deploy checks.** The test stack on the Pi (test Discord app and server) is where Discord-facing steps are verified. A step that needs a manual check in Discord says so in its report.
- **Feature flag** `GameNights:Enabled` defaults to off, so `prod` is unaffected until switched on there.
- **The stack config** lives in the private `alex-pi-1` repo under `stack/calcrony/`; env changes needed by a step (flag, role-to-tier ids, bot permissions) are listed in the step report and made there, not in this repo.

## 1. Goal and scope

Extend CalCrony (JJWren/CalCrony, MIT, .NET 10, Postgres/EF Core, Discord.Net bot, Blazor WASM web) so my Bristol gaming group can run tables, report results for Root and Arcs, and keep open-ended per-game leagues.

Phase 1 contains: Venues; Tables; Game library and roles; Game nights (per-game can teach / can play / new to it / not tonight) with Teach visibility; Result reporting for Root and Arcs; Leagues (open rating, optional seasons); Role-aware website (member and organiser views, league pages).

Not in phase 1 (do not build, but do not block): Arrange tables, availability polls, tournaments, check-in and scale tools, help guide, private games, moderator queue, full admin tier, TI4, swappable rating schemes.

One thing to build now though: the visibility flag on events and tables (public by default, with restricted and invite_only values reserved) and the rule that private rows can never carry a league. Retrofitting this later is the risky part. No private-game UI in phase 1.

## 2. Ground rules for every step

- Keep changes in separate extension modules where possible (for example `CalCrony.GameNights.*` projects or clearly named folders) so upstream merges stay easy. Touch existing CalCrony files only at the smallest possible seams, and list each such touch in `docs/FORK-NOTES.md`.
- Keep the MIT licence and copyright notice. Add our extension notice without removing the original.
- EF Core migrations only, one migration per step that changes the schema, applied at startup as CalCrony already does. Never edit an applied migration.
- All access control is enforced in the API, never only in the bot or web client.
- Anything the bot posts to Discord goes through CalCrony's existing delivery outbox (acknowledged after Discord succeeds). Do not call Discord from request handlers directly. Respect rate limits: lookups on demand, one post per round.
- Discord UI limits: buttons, select menus and modals are size-limited, so flows are stepped. Never assume more than 5 modal inputs or 25 select options.
- Every step ends with: build passes, tests pass, a short note appended to `docs/FORK-NOTES.md`, this file's checkbox ticked, and one commit with the message format `step N: <summary>`.
- If something in the repo contradicts an assumption here, stop and report instead of guessing.
- No localStorage-style shortcuts in the web client for anything the server must know.

## 3. Decisions already made (do not reopen)

- Tables hold 2 to 100 players. Size is configurable per game and per night; there is an optional host.
- Game type comes from existing Discord "interested in" roles (Onboarding answers), not tags. An admin marks which roles are game types.
- Roles map to tiers: member, organiser, moderator, admin. The API resolves tier from Discord roles; the web uses it for views, the API enforces it.
- Member names are not stored. Players display as "First L" (first name plus first letter of surname), read from the Discord server nickname when shown. Results store the Discord user id. A leaver shows as "Former member". Guests (no Discord account) get a minimal record with a typed "First L" name; this is the only real name we keep.
- Result reporting always asks for a photo of the final board. We never store it: from Discord it stays in the thread; from the website the bot forwards it to the thread and the API discards it. No file storage, no URL stored.
- League results and ratings are exempt from CalCrony's 90-day retention purge.
- Rating: pairwise multi-player Elo behind an interface (so the scheme can be swapped in a later phase).

## 4. Rating maths (for Step 9)

For a game with N players, rating R_i, placement-based score S_ij against each opponent j (1 if i placed better, 0.5 for a tie, 0 if worse):

```
E_ij = 1 / (1 + 10^((R_j - R_i) / 400))
ΔR_i = K / (N - 1) * Σ_{j≠i} (S_ij - E_ij)
```

Start rating 1000, K = 32, both configurable per game. Winner-only games: winner S=1 against all, others tie each other at 0.5 (or 0, decide in Step 9 and document it). Ratings are per game per league (per season if seasons are on).

## 5. Reporting field sets (game configuration, from James)

Games are configuration, not code. Each game defines a result type (ranked placement, single winner, or winning side), table size range, and a field list with a required flag and a field kind (player, pick-list, number, text).

**Root.** Required: players (name as First L), factions played, final scores. Optional: the faction left out of the draft, seat/turn order, map, landmarks used, deck used.

**Arcs.** Required: players, player colour (helps when reading the photo), final scores. Optional: leaders used, round the game ended (it is not certain the base game records this, so keep it optional).

TI4 is phase 3; do not add fields for it now.

## 6. Steps

Tick each box when done. Do not start a step until the previous one is ticked.

### [x] Step 0: Fork, recon and a green baseline

Goal: a working fork you understand, with the unknowns answered.

Tasks:
- The fork, `upstream` remote and branch model already exist (see section 0). Skip the fork/branch creation tasks.
- Follow the README to build and run locally (Postgres, API, Bot, Web) where possible; the **test stack on the Pi** (its own Discord test server and bot) is the dev environment. Keep secrets out of git.
- Read CONTRIBUTING.md and the code. Write `docs/FORK-NOTES.md` answering: how are the Api, Bot, Web and Contracts projects wired; how are bot commands, buttons and modals registered; how does the outbox work; how does Discord login and JWT work and what claims exist; how do events, attendee roles, restrict-to, waitlists, time polls, the activity log and the 90-day retention job work; can the event model carry an extra field or a related table without edits to core files.
- Test the open risk: how can the API learn a member's Discord roles (bot lookup of the guild member versus the OAuth `guilds.members.read` scope). Record what works and recommend one. The plan assumes a bot lookup mapped to tiers by the API.
- Test the second open risk: can the bot create private threads in the test server with its current permissions; record any needed permission.
- Run the existing tests and record the baseline.

Done when: build and tests are green, FORK-NOTES.md exists with the answers above, and any assumption in this plan that turned out wrong is listed at the top of that file.

### [x] Step 1: Extension scaffolding, tiers and the visibility flag

Goal: the seams everything else uses.

Tasks:
- Create the extension modules (API, Bot, Contracts, Web) and register them from the existing startup with minimal edits. Add a single feature flag `GameNights:Enabled`.
- Tier resolution: config table mapping Discord role ids to tiers (member, organiser, moderator, admin); a service that resolves a user's tier (role lookup via the bot, cached briefly and invalidated on expiry); tier added to the JWT claims or looked up per request, whichever FORK-NOTES recommends. Authorization policies for each tier.
- Visibility: add a Visibility value (public default, restricted, invite_only) to the extension's table/event extension rows, plus a shared query filter so non-invitees get not-found. Add the database check constraint so a visibility other than public can never be attached to a league entry (the league table arrives in Step 9; add the constraint then, but write the rule and tests for the filter now).
- Admin-only seed/config endpoints for the role-to-tier mapping.

Tests: tier resolution for each tier and for a member with no mapped role; policy tests for each endpoint family; visibility filter returns not-found to non-invitees.

Done when: a test endpoint per tier returns 200 for that tier and 403 for lower tiers, with no UI yet.

### [x] Step 2: Venues

Goal: reusable venues with default attendee limits and per-event overrides.

Tasks:
- Data: Venue (name, address, default attendee limit), VenueArea (venue, name such as "the upstairs", extra capacity, active), per-event override (limit, which areas are on).
- API CRUD (organiser and above write; members read).
- Link to CalCrony's existing event attendee limit and waitlist: the effective limit is the override if set, otherwise the venue default plus active areas. Do not reimplement waitlists; set the existing limit.
- Bot: show venue and limit on the event post; organiser buttons to switch areas on or off.
- Web: venue and area editor, limit shown with where it came from.

Tests: effective limit calculation; override beats default; toggling an area updates the CalCrony limit and the waitlist behaves.

Done when: an organiser can create a venue with an upstairs area, attach it to an event and see the limit change when the area is toggled.

### [x] Step 3: Game library (data and config)

Goal: games as configuration.

Tasks:
- Data: Game (name, result type, min and max table size within 2 to 100, default table size, rating K and start rating, rating enabled flag, optional Discord role id), GameField (key, label, kind, required, pick-list values, order), GameType (Discord role id marked as a game type by an admin) and GameRole link.
- Seed Root and Arcs from the field sets in section 5 using a JSON config file loaded at startup (idempotent upsert), so adding a game later is config only.
- API: list, get, create, edit, retire (organiser and above).
- Validation of a result submission against a game's fields lives here as a reusable validator (used in Step 7).

Tests: seed is idempotent; validator accepts a complete Root and Arcs submission and rejects missing required fields and out-of-range sizes.

Done when: GET games returns Root and Arcs with the right fields and required flags.

### [x] Step 4: Tables

Goal: tables on events, each with a Discord thread.

Tasks:
- Data: GameTable (event, game, size, optional host, status, thread id, visibility defaulting to public), TableSeat (table, Discord user id or guest id, seat order).
- API: create, update, seat and unseat players, set host (organiser and above; a seated member can see their table).
- Enforce size within the game's range and at most 100; configurable per game and per night (event-level default, overridable per table).
- Bot: creating a table posts a table thread via the outbox; the host is prompted first; seated players are mentioned. Commands: create table, join table, leave table.
- Web: table list on the event, own table page, table card.

Tests: size limits, host optional, seating the same user twice is rejected, thread post enqueued once.

Done when: an organiser creates a Root table of 4 for an event and the thread appears with the right people.

### [x] Step 5: Add-game and role flows (Discord and web)

Goal: the interactive way to add games, with role attach or create.

Tasks:
- Bot: an add game command that walks through steps (name, result type, table sizes, fields from a template, then a role step: attach an existing role via select menu or create a new one). Role creation needs the bot's Manage Roles permission and a position above the new role; check and show a clear warning if missing. Stepped, since modals are size-limited.
- A follow button on the game that toggles the game's role for the member.
- Admin marks which existing "interested in" roles are game types.
- Web: add-game wizard and games admin with the role step and permission warnings; follow toggle.

Tests: role create path and attach path; permission-missing path returns the warning rather than failing.

Done when: adding a game in Discord and on the web both produce a game with an attached role, and the follow button toggles it.

### [x] Step 6: Game nights and teach visibility

Goal: people say which games they can play or teach, and organisers see demand.

Tasks:
- Data: GameNightSignal (event, user, game, one of can_teach / can_play / new_to_it / not_tonight).
- Bot: event post gets a Pick games button leading to a stepped, per-game select; a summary on the event post showing demand per game and counts of fresh, experienced and teaching players.
- API: set and get signals; aggregate counts per game; teacher roster.
- Web: pick-games grid, live demand panel, experience list, roster, highlight games that have players but no teacher.
- Updating the event post must be throttled (batch updates, one edit per interval), since this scales toward 1,000 people.

Tests: aggregates; changing a signal replaces the old one; throttled update fires once per window.

Done when: several test users mark Arcs and Root, and the event post shows teachers and fresh-player counts.

### [ ] Step 7: Result reporting, backend

Goal: store results safely with the name and photo decisions applied.

Tasks:
- Data: Result (table, game, status pending, confirmed, disputed, voided, reporter, submitted at), ResultPlayer (result, Discord user id or guest id, placement, score, game-specific values), ResultFieldValue for table-level answers (map, deck, landmarks, round ended), GuestPlayer (typed First L name; nothing else). No photo field, no photo link.
- Names: a service that resolves display name from the Discord nickname in First L form (regex, tolerant of casing). If a seated player's nickname does not match, the API accepts a one-off typed name for that report in the request only and does not persist it against the member; the bot nudges the member to fix their nickname. Missing member shows as "Former member".
- State machine: reporter submits, other players confirm or reject; confirmed when all (or a configurable majority) confirm; reject moves to disputed and notifies organisers. Only confirmed results feed leagues.
- Validation through the Step 3 validator.

Tests: state transitions; validator wiring; nickname parsing cases (single name, hyphenated, no match); no name persisted for members; guest record is the only name stored.

Done when: a Root and an Arcs result can be submitted, confirmed and disputed through the API, and the database contains no member real names and no photo data.

### [ ] Step 8: Result reporting, Discord flow

Goal: the Report game button in the table thread.

Tasks:
- Add a Report game button to each table thread for any game whose result type is reportable (Root and Arcs now).
- Stepped prompt built from the game's field list: players and placements (select menus), required fields first, then optional ones the reporter can skip; scores via a modal (watch the 5-input limit, split if needed). Show a summary with Confirm and Edit before submitting.
- Always ask for a photo of the final board at the end of the prompt: the reporter uploads it in the thread. We neither download nor store it; it stays in Discord. The reporter may skip unless an admin makes it required (a per-game flag).
- After submit: post the result summary to the thread with Confirm and Reject buttons for the other players; handle timeouts with a reminder.
- Use the outbox for all posts.

Tests: a scripted run through the flow for each game with a fake Discord client; skip photo works; required photo flag blocks submission.

Done when: in the test server, a table of four can report a Root game, the others confirm, and the result appears as confirmed.

### [ ] Step 9: Leagues and ratings

Goal: open-ended per-game leagues with optional seasons.

Tasks:
- Data: League (one per game, open-ended), Season (optional, start and end), PlayerRating (league or season, Discord user id or guest id, rating, games played), RatingEvent (result, player, before, after).
- Rating interface `IRatingScheme` with the pairwise Elo implementation from section 4; unit-test it against hand-worked examples (two-player equals standard Elo; four players conserve total rating).
- Players join by playing: the first confirmed result creates their rating.
- Only confirmed results from public tables count. Add the database check constraint now so that a non-public table or event can never be linked to a league entry.
- Retention: exempt results, rating events and ratings from CalCrony's 90-day purge, with a test proving the purge leaves them.
- Bot: league standings and my-rating commands.
- Voiding or correcting a confirmed result recomputes ratings for affected games in order.

Tests: Elo maths, tie handling, season rollover, recompute after void, constraint rejects a private table in a league, retention keeps league rows.

Done when: three confirmed Root games produce a standings table whose ratings match hand-computed values.

### [ ] Step 10: Role-aware website

Goal: the web UI for all of the above, with views driven by tier.

Tasks:
- Using the existing Discord login, load the tier from the API and show member or organiser views (admin and moderator reuse the organiser view for now; the full admin tier is phase 3).
- Pages: venues, tables (list and own table), games admin, game night demand, league standings and rating history, player profile, result history, and a report form that uploads a photo to the API for forwarding.
- Photo from the website: the API forwards the file to the table thread through the bot and discards it; nothing is written to disk or the database. Verify with a test that asserts no file or blob remains.
- UI hides what a tier cannot use, but never relies on that: the API already enforces it.
- Names on every page come from the name service, never from stored real names.

Tests: component and API tests per tier; the photo pass-through leaves no residue.

Done when: a member sees only their allowed views, an organiser sees the admin pages, and a result reported from the web appears in the thread with the photo and in the standings once confirmed.

### [ ] Step 11: Hardening and handover

Goal: ready to deploy and to merge upstream.

Tasks:
- Full test run, and a load check with a few hundred simulated signals and event-post updates to confirm throttling holds.
- Review every endpoint for the tier check and visibility filter; add missing tests.
- Verify the privacy decisions with a script or test: no member real names in the database, no photo data anywhere, guest names deletable on request (add the delete endpoint if missing).
- Update the README with the extension's configuration (feature flag, role-to-tier mapping, game config file, bot permissions needed: manage roles, create threads).
- Merge `upstream/master` via `dev` (see `HOME.md`), resolve conflicts, rerun tests, and record the touch points in `docs/FORK-NOTES.md`.
- Write a short `docs/PHASE-2-NOTES.md` listing what is intentionally missing (arranger, polls, tournaments, check-in, help guide, private-game UI) and which hooks were left for each.

Done when: all tests pass after merging upstream, and the notes are written.

## 7. How to resume

When you open a new session, read this file, find the first unchecked step, re-read `docs/FORK-NOTES.md`, check the PR checks on `dev`, and continue from there. Do not redo ticked steps.

## 8. Open questions that may need my answer mid-build

Ask at the point they bite, with a recommendation:

- Who may press Arrange tables (phase 2; no action now).
- Whether the "confirm" rule should be all players or a majority.
- What the photo is for (the table checking the result, or reading scores into the form). Current build treats it as the table checking the result.
- Whether the "First L" nickname convention is acceptable to the group.
- Whether very large tables should report only the winning side (phase 3).
