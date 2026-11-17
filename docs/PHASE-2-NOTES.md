# Game Nights: what phase 1 leaves out, and the hooks left for it

Phase 1 (PLAN.md steps 0 to 11) is venues, tables, the game library, game-night answers with teach visibility, result reporting for Root and Arcs, open per-game leagues and the role-aware website. This file lists what is **intentionally missing** and **which hooks were left** for each item, so a later phase starts from the seam and not from a search. Design decisions that are settled live in `docs/FORK-NOTES.md`; this file only points at them.

## Before this goes to prod (not phase 2, but not done)

- **Privacy page.** Game Nights keeps a member's Discord id and a derived "First L" name against results, and a guest's typed "First L". The owner agreed this is fine; the paragraph on `Privacy.razor` is not written. Do it before switching the flag on for anyone but the owner's own server (README says the same).
- **Not exercised against real Discord or a browser.** The report flow (user select, forms, buttons), table threads and the website pages were verified by scripted and component tests only. Walk through them once on the test stack: add a game, open a table, report a Root game with four people, confirm, read the standings.
- **Role-to-tier mapping has no screen.** It is set through the API (`PUT .../tier-roles`, in the README). A settings page for it is the first thing an admin will ask for; the routes and tests are there, only the page is missing.
- **Invite permissions for prod.** The prod bot must have Manage Roles and the thread permissions (README). Re-invite or grant by hand.

## Intentionally missing

### Arrange tables (the arranger)
Suggesting who sits where from the night's answers. Not built: phase 1 shows the demand and the teacher roster, and organisers seat people by hand.
- **Data it would read:** `GameNightSignals` (per member per game: can teach, can play, new to it, not tonight), through `SignalMath` / `GET events/{id}/signals/people` (organiser tier); table capacity from `TableRules` and `EventLimits`.
- **Where it would write:** `POST events/{id}/tables` and `POST tables/{id}/seats` already do everything an arranger needs to apply a plan; it can be a pure function from answers to a list of `CreateTableRequest`s with `SeatUserIds`.
- **Seam left:** `TableDto.HostUserId` (the teacher), `CreateTableRequest.SeatUserIds`, and the roster endpoint.

### Availability polls
"Which night suits you" before a night exists. Not built.
- **Seam left:** CalCrony's own polls (`PollEndpoints`, core) are untouched and work as normal; a game-night poll would be a poll whose winning option creates an event. Nothing in Game Nights depends on how an event came to exist.

### Tournaments
Brackets, rounds, byes, scoring across a day. Not built.
- **Seam left:** a result is already a stored, confirmable record with a table, a game and per-player placements; a round would be a set of tables on one night. `GameNightTables.InLeague` and `GameNightEventExtensions.InLeague` keep league and non-league games apart, so a tournament game can opt out of ratings by leaving the flag off. The same flag has database check constraints tying it to public tables and nights (`VisibilityRules.CanCarryLeague`).

### Check-in and scale tools
Arrival check-in, door counts, big-night tooling. Not built.
- **Seam left:** seats are by Discord id (`GameNightTableSeats`), and table size is configurable to 100 (`GameRules.AbsoluteMaxSize`); the load test (`GameNightsLoadApiTests`) shows a few hundred answers to one night stay correct and produce one Discord edit. A check-in flag would be one more column on the seat.

### Help guide
In-app and in-Discord guidance. Not built (the README section and these notes are the only guide).
- **Seam left:** the website pages carry their own plain-language hints; `/help` in the bot is core and untouched.

### Private games (UI and rules)
Phase 1 builds only the *flag*, never the UI.
- **Seam left, all in place:** `GameNightsVisibility` (`Public`, `Restricted`, `InviteOnly`) on tables and nights; `GameNightInvites` (scope, scope id, user); `VisibilityFilter` / `VisibilityRules.CanSee` applied by every read (tables, results, signals, venues' event routes), turned into 404 for anyone who cannot see; and the database constraint that a private table or night can never be in a league. **Provisional semantics, nobody has used them:** Restricted = invitees plus organisers and up; InviteOnly = invitees plus moderators and up. Decide the real rules, then add the screens and the invite routes.

### Moderator queue and the full admin tier
Moderators settle disputes one result at a time from the Results page; there is no queue, no audit view and no per-moderator history.
- **Seam left:** disputed results are queryable (`GET results?status=2`), `ResultDisputed` already notifies the roles mapped to organiser and above once per dispute, and `POST results/{id}/resolve` is the one place a moderator acts. Admin-only configuration (`tier-roles`, `game-types`, `games/seed`) exists as routes; its screens do not.

### TI4 and other games
Games are configuration (`games.json` / `GameNights__GamesFile`), so a game with the existing field kinds needs no code. TI4 was deliberately not added to the file. Things it may need that do not exist: factions-by-player with expansions, a timed or round-based end, a "winning side" with more than two sides.
- **Seam left:** `GameResultType` (ranked, single winner, winning side) and `GameFieldKind` (player, pick-list, number, text) are the vocabulary; extending either means touching `ResultValidator` (shared by API, bot and web), the bot's `ReportFlow`, and the web's `ReportDraft` together. The validator tests are the safety net.

### Swappable rating schemes
Only pairwise Elo is built.
- **Seam left:** `IRatingScheme` (Contracts) with `PairwiseElo`; the one place the scheme is chosen is `LeagueEngine.Scheme`. Ratings are *replayed* from confirmed results, so a new scheme re-rates history by running a recompute (`POST games/{id}/league/recompute`). Per-game K and start rating are in the game definition; there is no editor beyond editing the game.

## Smaller things worth knowing

- **Web photo upload was dropped** (decision in FORK-NOTES, Step 10). Putting it back needs somewhere to hold the bytes that is not the database or disk (for example API memory with a one-time fetch token the bot redeems), because the API never calls Discord and the outbox is a database table.
- **The result confirm rule is a setting** (`GameNights__ConfirmMode`): `All` by default. Revisit after a few real nights.
- **Guest names live in two places that must stay in step:** `GameNightGuests` and the results' guest rows (by id only). Erasing blanks the name and keeps the row so past results keep their shape ("Former guest").
- **Names refresh slowly by design:** a matched name is refreshed after 12 hours, an unmatched one after an hour, so a fixed nickname shows within about an hour.
- **Per-night answers go with the event.** They are not kept as long-term preferences; "what do I usually like to play" would be a new table.
