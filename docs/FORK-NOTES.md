# Game Nights extension: fork notes

Working notes for the Game Nights extension (see `/PLAN.md`). Step 0 recon is below; each later step
appends a short section. File references are `path:line` at the time of writing (2026-11-16, `dev`
after PR #4).

## Plan assumptions that turned out wrong (or need a decision)

1. **No extension seam exists.** There is no plugin pattern anywhere. Every touch point is a hard-coded
   core edit, so each "minimal seam" must be created once. The full list is in section 12.
2. **"The API resolves tier from Discord roles via a bot lookup" contradicts ADR 0001/0004.** The API
   never calls Discord. The bot pushes role *snapshots* for **watched** roles only, and the web path
   fails closed when the snapshot is older than 30 minutes. Recommendation in section 7.
3. **Bot calls are fully trusted.** Every API guard starts with `if (context.User.IsBot()) return null;`
   and the API key carries no user identity. "All access control is enforced in the API" can only be true
   for web callers; for Discord callers the bot must check and the API trusts it (the ADR 0004 model,
   `RoleRestrictionGate`). Plan rule 3 needs this carve-out.
4. **Member names: there is no mechanism to show "First L".** The API stores no nicknames; the web shows
   `user {id}` for everyone but the viewer (`EventDetail.razor:758`). Pushing nicknames would be a new
   per-user Discord fact needing privacy-policy disclosure (ADR 0004 says the same of role snapshots).
5. **Docker builds break on any new project.** `src/CalCrony.{Api,Bot,Web}/Dockerfile` copy only named
   csproj files before restore. A new `CalCrony.GameNights.*` project needs those lines and
   `CalCrony.slnx` edited. This is why section 6 recommends *folders inside the existing projects*
   rather than new projects.
6. **The existing attendee limit is `RsvpOption.Capacity` on the attending option**, changed only via the
   event update path, which also promotes the waitlist (`EventEndpoints.cs:1239`, `RsvpPolicy.PromoteAsync`).
   Step 2 must go through that path; writing the column directly would not promote anyone, grant roles,
   or add thread members.
7. **Bot invite permissions are missing two bits we need.** `DiscordInvite.Permissions = 335275969536`
   has ManageRoles, ManageThreads, CreatePublicThreads, SendMessagesInThreads but **not**
   CreatePrivateThreads or AttachFiles. Changing it means updating the pinned test and README, and
   existing installs (prod) must re-authorise. The test bot already has both (section 8).
8. **Retention exemption is free.** The purge only touches the tables it lists (section 5), so league
   tables are exempt by omission. The plan's "exempt" test should simply pin that.
9. **The Discord outbox is a closed enum plus an if-chain** (section 3). A new delivery type needs a
   Contracts edit and a poller edit, and an unknown type retries 10 times then fails, so deploy bot and
   API together (our pull-based deploy does).
10. **Web pages in another assembly are not routed** without `AdditionalAssemblies` on the Router
    (`App.razor:1`).

## 1. Wiring

- `Contracts` references nothing; `Api`, `Bot`, `Web` each reference only Contracts. They never reference
  each other. Web is Blazor WASM, so everything in Contracts ships to the browser.
- Bot to API: typed `HttpClient` `CalCronyApiClient` (`Bot/Program.cs:20-25`), static `X-Api-Key`, every
  call wrapped in `ApiResult<T>` (`Bot/Api/CalCronyApiClient.cs`). Mutations send `X-CalCrony-Actor` for the
  action log only.
- Api `Program.cs` is flat top-level code: DbContext `:23-26`, services `:28-51`, auth/policies `:55-101`,
  hosted services `:116-127` (`StartupMigrationService` first), 19 hard-coded `app.MapXxxEndpoints()` at
  `:164-182`. The only per-feature convention is a static `XxxEndpoints.MapXxxEndpoints`.
- Bot `Program.cs` (38 lines) registers the socket client, `InteractionService`, five singletons and three
  hosted services.
- Existing config toggles are the precedent for `GameNights:Enabled`: `Scheduler:Enabled`,
  `Retention:Enabled`, `Database:AutoMigrate`.

## 2. Bot commands, buttons, modals

- Discord.Net `InteractionService`; modules are loaded with one call,
  `AddModulesAsync(typeof(DiscordBotService).Assembly, ...)` (`DiscordBotService.cs:50`). Only the Bot
  assembly is scanned.
- Slash modules: `InteractionModuleBase<SocketInteractionContext>` in `Bot/Modules/*.cs`, with
  `[CommandContextType(Guild)]`, `[RequireBotInGuild]`, `[Group(...)]`. Manager checks are inside handlers.
- Components: `[ComponentInteraction("pollvote:*:*")]`, custom ids `prefix:{guid}[:{guid}]`. Select menus bind
  `string[]`. Modals: an `IModal` class opened with `RespondWithModalAsync<T>`, handled by
  `[ModalInteraction("polladdmodal:*")]`. A modal must be the first response (no defer or API call before).
- Autocomplete: `AutocompleteHandler` subclasses in `Bot/Api/`, attached per parameter with
  `[Autocomplete(typeof(X))]`, no registration step.
- Commands register once in `OnReadyAsync` (`:73-86`): guild-scoped when `Discord:TestGuildId` is set
  (our test stack sets it), otherwise global. All-or-nothing; no per-module scoping.

## 3. Outbox

- `Delivery` (`Api/Data/Entities.cs:508`): `Type` (int), `ChannelId`, `PayloadJson` (max 8192), `DueAt`,
  `Status` (Pending/Sent/Failed/Cancelled), `Attempts`, `ClaimedAt`, `RecipientUserId`.
- Any API code adds a row in its own unit of work (`EventEndpoints.cs:335`, `DeliveryScheduler.cs:235`).
  Coalescing precedent: `LiveListSync.cs:27-67` (match pending, `Attempts==0`, same payload). `DueAt` may be future.
- Bot: `DeliveryPollerService` ticks every 15 s, `GET /deliveries/pending?limit=20`, posts, then
  `POST /deliveries/{id}/ack`. Exception = no ack = retry. After 10 attempts the row is Failed. At-least-once.
- Closed `DeliveryType` enum (values 0-16, `Contracts/Deliveries.cs`) with a payload record each; the bot
  dispatches in a hard-coded if-chain (`DeliveryPollerService.PostAsync`, `:92-252`), unknown = throw.
- To add a type: append enum + record, add a branch, enqueue. Convention: best-effort types return true;
  posting types throw when the channel is missing; record ids back via the API with a compensating delete.
- Rows are purged after 90 days, so the outbox is **not** a store for results.

## 4. Auth

- Schemes: `ApiKey` (default, bot: claim `client=bot`) and JwtBearer. Policies: fallback = authenticated,
  `BotOnly`, `UserOnly`. No role/tier policies.
- Login: `/auth/discord/start`, `/callback`, `/refresh`, `/logout`. Scopes `identify guilds` only
  (`DiscordAuthProvider.cs:28`). The Discord token is dropped after login. `UserGuildMembership` rows are
  replaced; `UserProfile.Username` is upserted.
- JWT: HS256, 30 min, issuer `CalCrony.Api`, audience `CalCrony.Web`. **Claims: `sub` (Discord id),
  `name`, optional `avatar`**. No guild, role or tier claims. Refresh token: 32 random bytes, hashed,
  30 days, rotated, HttpOnly cookie `calcrony_refresh`.
- Web guild access: `GuildAccessService` reads the `UserGuildMembership` snapshot (refused if older than 7
  days) and returns None/Stale/Member/Manager. `CanManage` = owner or ManageGuild.
- Guards (`EventEndpoints.cs:82,245,276,306`) are reusable; reading an event as a non-member returns 404.

## 5. Domain

- Single `CalCronyDbContext`, all entities in `Api/Data/Entities.cs`. `Event.GuildId` is a plain `long`
  (no FK to Guilds anywhere); Options/Rsvps/Notifications cascade from `Events`.
- Attendee roles are per RSVP option (`RsvpOption.AttendeeRoleId`), granted/revoked via outbox rows.
- Restrict-to: `RsvpOption.AllowedRoleIds`, `Poll.AllowedRoleIds`; `RoleRestrictionGate` (bot trusted, web
  answered from snapshots, entry-only).
- Attendee limit: `RsvpOption.Capacity` (nullable). Enforced in `PutRsvp` under `SELECT ... FOR UPDATE`
  (`LockEventRowAsync` is `private`). Waitlist = `Rsvp.Waitlisted` ordered by `CreatedAt`; promotion runs
  only from endpoint code (`RsvpPolicy.PromoteAsync`).
- Time polls: `Poll`, `PollOption.SlotAt`, `PollVote`, `POST /polls/{id}/convert`.
- Activity log: `ActionLog.Record(...)` in the caller's unit of work; `ActionLogAction` and
  `ActionTargetType` are closed, append-only, int-stored enums in Contracts.
- Retention (`Services/RetentionService.cs:27-70`) purges only: non-pending deliveries, login states,
  refresh tokens, calendar link tokens, action log entries (90 days), and trims role snapshots. Events,
  RSVPs, polls, series and templates are never purged. There is no allow-list: exemption = not listed.

## 6. Extensibility (EF)

- One context, one migration chain in `Api/Data/Migrations/` (24 migrations, latest
  `AddAvailableHours`), applied by `StartupMigrationService`. No `IEntityTypeConfiguration`, no query filters.
- A new entity needs a `DbSet` and an `OnModelCreating` block in the core context: **a core edit,
  unavoidable**. Extension tables can still FK to `Events(Id)` without editing the `Event` class.
- **Recommendation:** keep the extension in a `GameNights/` folder in each project, and make the
  unavoidable core edit a single call, `modelBuilder.ApplyGameNightsModel()`, with the `DbSet`s declared in
  a `partial` context file (a one-word core edit). Migrations stay in the single chain (one per schema step,
  as the plan says). A second DbContext is not precedented and would need its own history table and
  migrate step; not recommended.

## 7. How the API learns a member's Discord roles: recommendation

| Option | Verdict |
|---|---|
| Bot REST lookup of the guild member (works: the test bot lists members with roles) | Works for the bot, but the API cannot call Discord (ADR 0001/0004). |
| OAuth `guilds.members.read` | Rejected by ADR 0004: new consent per guild, web callers only. |
| **Extend the existing role snapshots** (`GuildMemberRole`, bot-pushed) | **Recommended.** Reuses sync, the 10-minute reconcile and the fail-closed lease. |

How: add the configured tier role ids to the watched set. `RoleWatchList.WatchedAsync` is the only place,
and it is derived (not a table), so it needs one new union with the tier-mapping table. Without that,
retention drops the snapshots (`RoleSnapshotEndpoints.cs:306-308`) because tier roles are not referenced by
any event or poll. Consequences to accept: web tier checks fail closed after 30 minutes without a bot
sync, and **tier is looked up per request, not put in the JWT** (a JWT claim would outlive a role
removal by up to 30 minutes and would need role data at login, which we do not have). Bot-initiated calls
check tier bot-side from the live socket cache, as `RoleRestrictionCheck` does.

Live check (2026-11-16, test guild, read-only REST with the test bot): the member list returns roles for 3
of 4 members. No members had server nicknames, so the "First L from nickname" convention is **untested
against real data**; a couple of nicknames need setting in the test guild before Step 7.

## 8. Private threads and permissions

- Existing: `EventThreadManager` creates **public** threads on the event message (`CreatePublicThreads`).
  No generic create-thread delivery exists; the bot creates the thread and then records the id.
- Intents: `Guilds | GuildMembers` (privileged, enabled). No MessageContent.
- Test bot, live (read-only, 2026-11-16): guild permissions include ManageRoles, ManageThreads,
  CreatePublicThreads, **CreatePrivateThreads**, SendMessagesInThreads, AttachFiles; ManageNicknames is not
  held. So private threads are permitted in the test guild. **Not exercised:** no thread was actually
  created (it would post into the test server); Step 4 creates the first one.
- The **invite integer does not include CreatePrivateThreads or AttachFiles** (item 7 at the top). Tables
  can use public threads (an existing capability) in phase 1; private threads and photo forwarding need the
  integer change and a re-invite on prod. Forwarding a photo needs AttachFiles.
- Role creation (Step 5) needs ManageRoles (present) and the bot's role positioned above the new role.

## 9. Baseline

CI run on `dev` at `9180798` (PR #4), `build-test` green: **1305 tests, 0 failed** (721 + 425 + 159 across
the Api, Bot and Web test projects; Api tests need Docker/Testcontainers Postgres, so they run only in CI
here). Local runs are not possible (no .NET SDK); CI is the compiler.

## 10. Web

- `CalCronyWebApiClient` typed client with `ApiResult<T>`; routes under `/app/guilds/{GuildId:long}/...`;
  per-page `[Authorize]`. The guild tab strip is hard-coded (`Components/GuildTabs.razor:5-8`), the sidebar
  is in `Layout/NavMenu.razor`. `CanManage` comes from `GetMyGuildsAsync`.
- New area, minimal edit: new `@page` components in the Web project plus one edit each to `GuildTabs.razor`
  and `NavMenu.razor`. A separate assembly would also need `AdditionalAssemblies` and a Dockerfile edit.

## 11. Tests

xunit; Api tests use `WebApplicationFactory<Program>` + Testcontainers Postgres (`ApiFixture`;
`WebAuthFixture` fakes Discord login); Bot tests are pure unit tests; Web tests use bUnit 2.x.
`Bot.Tests/ModuleContextTests` reflects over the Bot assembly only, so extension Bot modules must live in
the Bot assembly (a folder) to be covered by it.

## 12. Core touch points the extension will need (added as steps land)

| Seam | Edit | Step |
|---|---|---|
| `Api/Program.cs` | `AddGameNights(config)` and `MapGameNightsEndpoints()`, behind `GameNights:Enabled` | 1 |
| `CalCronyDbContext` | one `ApplyGameNightsModel()` call (and `DbSet`s) | 1+ |
| `RoleWatchList.WatchedAsync` | union tier role ids | 1 |
| `Bot/Program.cs` | `AddGameNights()` registration | 1 |
| `DeliveryType` + `DeliveryPollerService` | new delivery types and branches | 4+ |
| `RetentionService` | none (exempt by omission; add a pinning test) | 9 |
| `GuildTabs.razor`, `NavMenu.razor` | new tabs/links | 2+ |
| `DiscordInvite.Permissions` + test + README | add CreatePrivateThreads, AttachFiles if needed | 8/10 |
| `ActionLogAction`, `ActionTargetType` | append values | as needed |

## Open decisions for the owner (from this recon)

1. **Folders vs new projects.** Recommend folders inside Api/Bot/Web/Contracts (`GameNights/`), because new
   projects break the Dockerfiles and the Bot module scan and test pin. This deviates from the plan's
   "separate projects where possible" wording ("or clearly named folders" is already allowed).
2. **Tier lookup per request from role snapshots**, not JWT claims (section 7).
3. **Nickname storage.** To show "First L" on the web the bot must push nicknames, a new stored per-user
   fact. Alternative: show names only where the bot renders them (Discord posts), and on the web only if
   the owner accepts the disclosure change. Decide before Step 7.
4. **Photo forwarding and private threads need the invite-permission change** (and a re-invite on prod).

---

# Step 1: scaffolding, tiers and the visibility flag

**What landed.** Folder `src/CalCrony.Api/GameNights/` and `src/CalCrony.Contracts/GameNights/`; one
migration (`GameNightsFoundation`, three additive tables); feature flag `GameNights:Enabled` (default
off: no services, no routes; the tables exist regardless, empty).

**Tiers.** `Member < Organiser < Moderator < Admin`, mapped from Discord roles per guild
(`GameNightsTierRoles`). Resolved **per request** by `TierResolver` from the bot's role snapshots
(decision 2): the bot counts as Admin (it checks Discord live); a web caller must be in the guild; a
**server manager (Manage Server or owner) is Admin**, so a fresh server can set the mapping before any
role exists; otherwise the highest mapped role held wins. If the snapshot cannot answer (guild not
synced, marker older than 30 minutes, or a mapped role not yet checked) the tier is *unverified*:
Member-level routes still work, everything above is refused with **409 `tier_unverifiable`**.
Deleted mapped roles (null-name tombstone) stop counting.

**Gate.** `.RequireTier(tier)` is an endpoint filter, not an ASP.NET policy (plan said policies): it needs the
route's `guildId` and has two refusal shapes. Probes: `GET /guilds/{id}/game-nights/probe/{member|organiser|moderator|admin}`
and `/me`. Admin config: `GET|PUT /guilds/{id}/game-nights/tier-roles` (PUT replaces the mapping; Member is
not mappable; a role maps to one tier; max 50).

**Visibility.** `GameNightEventExtensions` (EventId PK, FK to `Events`, cascade; no row = public),
`GameNightInvites` (scope, scope id, user). `VisibilityFilter.VisibleTo`/`CanSeeEventAsync` mirror the pure
`VisibilityRules.CanSee`; callers turn "not visible" into 404. **Provisional semantics** (no UI yet):
Restricted = invitees + organisers up; InviteOnly = invitees + moderators up. `VisibilityRules.CanCarryLeague`
(public only) is written and tested now; the database check constraint arrives with the league table (Step 9).

**Core touch points (all of them).**

| File | Edit |
|---|---|
| `Api/Program.cs` | `using`, `AddGameNights(config)`, `MapGameNights()` |
| `Api/Data/CalCronyDbContext.cs` | `partial` keyword; `GameNightsModel.Apply(modelBuilder)` |
| `Api/Services/RoleWatchList.cs` | union the tier-mapped roles into the watched set |
| `Api/Data/Migrations/*` | new migration + snapshot (generated) |
| `tests/.../WebAuthFixture.cs` | un-sealed so the Game Nights fixture can turn the flag on |

**Deviations from the plan.** (1) Folders, not new projects (owner agreed). (2) The Bot and Web modules are
**not created yet**: nothing in them would do anything until Step 2 (event post buttons, venue editor), so they
start there rather than as empty shells. (3) Policies are endpoint filters (above). (4) Tier is per request, not
a JWT claim (owner agreed).

**Config needed on the Pi.** None to deploy (flag off). To try it on the test stack: `GameNights__Enabled=true` on
the API in `~/calcrony-test/.env`/compose, then `PUT /guilds/{id}/game-nights/tier-roles` with the test server's
role ids. Until the bot's next reconcile (every 10 minutes) checks the new roles, tiers above Member answer 409.

**Verification.** Local .NET 10 SDK installed in `C:\Users\alexp\dotnet10-sdk` (user folder, not on PATH); the
solution builds. Pure-rule tests pass locally; the database-backed tests need Docker, which was not running,
so they run in CI.

---

# Step 2: venues

**What landed.** Migration `GameNightsVenues` (additive): `GameNightVenues`, `GameNightVenueAreas`,
`GameNightEventAreas`, plus `VenueId` and `LimitOverride` columns on `GameNightEventExtensions`.

**Rule.** Effective limit = the event's override if set; otherwise the venue's default plus the capacity of
the areas switched on for that event; a venue with no default means no limit; no venue and no override means the
event's own limit is left alone (`VenueLimitRules`, shared in Contracts). Retired areas stop counting.

**Applying it.** `EventLimits.ApplyAsync` sets the attending option's `Capacity` the way the event edit does:
takes the same event-row lock, refuses a limit below the seats already taken (409 with the core wording),
calls `RsvpPolicy.PromoteAsync` so the waitlist is seated, queues the Discord re-render and the live-list sync.
It does **not** skip bot callers (unlike the core embed-sync helper), because the bot's venue buttons change data
the bot has not rendered itself. Editing a venue's default or an area's capacity/retirement re-applies to every
live event using that venue with no override; if any would unseat someone the whole edit is refused (409).

**Where the venue shows.** Attaching a venue writes `Event.Location` (`Name, Address`), so the Discord post,
the web page and the calendar feed show it with no change to those surfaces; the limit already shows in the
existing "going" count. Detaching leaves the location and the limit as they were.

**Routes** (`/guilds/{id}/game-nights/...`): `GET venues` (member; `?includeRetired`), `POST venues`,
`PATCH|DELETE venues/{v}`, `POST venues/{v}/areas`, `PATCH|DELETE venues/{v}/areas/{a}` (organiser up),
`GET events/{e}/venue` (member, visibility-filtered: hidden = 404), `PUT events/{e}/venue` (organiser up; venue,
override, areas), `PUT events/{e}/venue/areas/{a}` (organiser up; `{on}`). A venue or area an event has used is
retired, not deleted (409).

**Bot.** `/venue areas event:` (organisers, checked bot-side from live roles via `BotTier`) opens a private panel:
limit, where it came from, one toggle button per area (`gnarea:{event}:{area}:{1|0}`). The buttons live in an
ephemeral panel, not on the public event post, which has no free component rows. Commands are guarded by
`RequireGameNights` (flag off = polite refusal). Bot extension code is a `GameNights/` folder in the Bot assembly,
so `ModuleContextTests` covers it.

**Web.** `Venues` tab (only when the feature is on), `/app/guilds/{id}/venues` (members read; organisers add, edit,
retire, delete venues and areas), and a Venue panel on the event page (everyone sees venue + limit + why;
organisers pick the venue, override, and open/close areas). The extension components resolve their client
optionally, so a host without it renders nothing and existing page tests are untouched.

**Limitations (phase 1).** The limit is set on the one event, not on a recurring series' template: the next
occurrence spawns from the template and needs the venue attached again. Venue choice is on the web only; Discord can
only open and close areas.

**Core touch points added this step.** `Bot/Program.cs` (using + `AddGameNights()`), `Web/Program.cs` (client
registration), `Web/Components/GuildTabs.razor` (one `<VenuesTab>`), `Web/Pages/App/EventDetail.razor` (one
`<EventVenuePanel>`), `Web/_Imports.razor` (one using), and the generated migration + snapshot.

**Verification.** Local: Bot 438 and Web 169 tests pass; the solution builds. The Api venue tests need Docker (not
running locally), so they run in CI.

---

# Step 3: game library (data and config)

**What landed.** Migration `GameNightsGames` (additive, four tables): `GameNightGames`, `GameNightGameFields`,
`GameNightGameTypes`, `GameNightGameTypeLinks`. Games are per server (unique on server + key), retired never deleted.

**One shape everywhere.** `GameSpec` (Contracts) is the whole definition: key, name, result type (ranked placement,
single winner, winning side), table size 2 to 100 with a default, rating on/off with K and start rating, an optional
follow-role id, the game-type roles it belongs to, and its result questions (`GameFieldSpec`: key, label, kind
player/pick-list/number/text, scope table/player, required, unique, pick-list values). The create route, the edit
route, the config file and the validator all use it. `GameRules.Validate` checks a definition (sizes, keys, pick
lists need 2 to 50 distinct values, one player list, "unique" only on per-player pick-lists and text).

**Config file.** `GameNights/games.json` is built into the API (Root and Arcs, from section 5 of the plan) and can
be replaced with `GameNights:GamesFile=/path/games.json`, so adding a game is editing JSON. Each game carries a
`version`. Applying the file is idempotent: a missing game is created; an existing seeded game is refreshed only when
the file's version is newer AND nobody has edited it by hand (`LocallyEdited`); a refresh never touches the role or
type links an admin attached. It runs (a) at startup for the servers in `GameNights:SeedGuildIds` (comma-separated),
after the host has started so migrations are done, and never stops the API starting if it fails; (b) on demand via
`POST /guilds/{id}/game-nights/games/seed` (Admin).
Starter lists: Root factions are the ten I am sure of (base, Riverfolk, Underground, Marauder, Homeland), maps
Autumn/Winter/Lake/Mountain, decks Standard and Exiles and Partisans; Arcs colours Red/Yellow/Blue/White, leaders
free text. Check the lists against the table's actual boxes and edit the file (or the game) as needed.

**Routes** (`/guilds/{id}/game-nights/...`): `GET games` (member; `?includeRetired`), `GET games/{id}`, `POST games`,
`PUT games/{id}` (whole definition; the key cannot change; fields are matched by key), `POST games/{id}/retire|reinstate`
(organiser up); `POST games/seed`, `PUT game-types` (admin); `GET game-types` (member). A game may only name game
types an admin has listed; removing a type a game still uses is refused (409).

**Validator.** `ResultValidator.Validate(GameSpec, ResultSubmission)` (Contracts, pure, shared with bot and web)
is the one Step 7 will call. Rules: player count within the game's sizes; nobody listed twice; each player is a
member or a guest; placements follow the result type; required answers present (per player and per table);
pick-list answers on the list; numbers whole; text at most 200 characters; "unique" questions have no repeated
answer (two players cannot both be the Marquise or both be red); unknown question keys rejected.
Convention for later steps: placement is a separate field on each reported player (not a question), and the score is
the question keyed `score`.

**Core touch points added this step.** `CalCrony.Api.csproj` (one embedded resource), the generated migration and
snapshot. Everything else is in `GameNights/`.

**Verification.** Local: solution builds; the pure tests (25) pass. The database-backed API tests need Docker and
run in CI.

---

# Step 4: tables

**What landed.** Migration `GameNightsTables` (additive): `GameNightTables`, `GameNightTableSeats`, and a nullable
`DefaultTableSize` on `GameNightEventExtensions`. A seat holds a member OR (from result reporting on) a guest;
a check constraint makes it exactly one, so Step 7 does not have to rewrite the table. A table is numbered 1, 2, 3
within its night (unique), keeps its history when cancelled, and has a `Visibility` (public by default; the filter
treats a non-public table as not found unless you are seated, invited, or your tier reaches it).

**Rules.** Size = the request's, else the night's default (pulled into the game's range), else the game's default;
always inside the game's range and never above 100. A table can't shrink below its seated count. A host is optional,
must be seated, and standing up gives hosting up. Seating the same person twice is refused (409); a full table
turns people away (409). Members can sit and stand only themselves; organisers can seat anyone; the bot is trusted
(it checks Discord first). Tables take seat changes only while the night is live and the table is open. A person is
not stopped from sitting at two tables on one night (a two-game evening is normal).

**Routes** (`/guilds/{id}/game-nights/...`): `GET events/{e}/tables` (member, visibility-filtered; `?includeCancelled`),
`POST events/{e}/tables`, `PUT events/{e}/table-defaults`, `PATCH tables/{t}`, `POST tables/{t}/cancel` (organiser up),
`GET tables/{t}`, `POST tables/{t}/seats`, `DELETE tables/{t}/seats/{user}` (member for self), `PUT tables/{t}/thread`
(bot only).

**Threads through the outbox.** Two new delivery types in the core enum: `CreateTableThread = 17` and
`TableSeatChange = 18`. Creating a table enqueues exactly one `CreateTableThread` in the same transaction; reading or
editing never enqueues another. The bot reads the table first and does nothing if it already has a thread, is closed
or is gone, creates the thread on a starter message in the night's channel, records the thread id with the API
straight away, and removes the post again if that fails, so a retry cannot leave a second thread. Then the welcome:
the host is addressed first (the only ping in that message), then the other players are mentioned by id; each
message pins `AllowedMentions` to exactly the people it names. Later seat changes enqueue a short note only once the
table has a thread (the thread's own welcome lists the seats made before it). Seat notes are best-effort and always
acknowledge; a thread deleted in Discord is forgotten (id cleared).
**Public thread, not private:** the invite integer lacks Create Private Threads (see the top of this file). Tables
use public threads on a starter message, which is what event threads already do. Moving to private threads is the
invite change flagged earlier (and a re-invite on prod).

**A new seam.** `IExtensionDeliveryHandler` (Bot): the poller asks each registered handler whether it owns a
delivery type before its own dispatch gives up. Later steps (game-night signals, result reminders) reuse it.

**Bot.** `/table create event game [size] [host]` (organisers, checked bot-side), `/table join table`, `/table leave
table`, with autocompletes for games and for open tables at upcoming nights. Guarded by `RequireGameNights`.

**Web.** A Tables section on the event page (a card per table; organisers also get the night's default size and an
"Open a table" form), a table page at `/app/guilds/{id}/tables/{table}`, and a card with Sit down / Leave table, and
for organisers seats, host (chosen from the people sitting) and Cancel table. Components resolve their client
optionally and render nothing when the feature is off. **People show as "user {id}"**, exactly as the RSVP list does,
until the name service arrives in Step 7.

**Not done (deliberately).** Cancelling a table does not archive its thread. Choosing a host is possible on the
website and the API; the bot's `create` takes one but there is no `/table host`. Guests can't be seated yet.

**Core touch points added this step.** `Contracts/Deliveries.cs` (two enum values), `Bot/DeliveryPollerService.cs` (one
constructor parameter and one dispatch loop), `Web/Pages/App/EventDetail.razor` (one `<EventTablesPanel>`), and the
generated migration + snapshot.

**Verification.** Local: solution builds; Bot 452 and Web 178 tests pass. The Api table tests need Docker and run in CI.

---

# Step 5: add-game and role flows

**What landed.** Migration `GameNightsGameRoles` (additive): `RoleState` and `RoleNote` on `GameNightGames`.
Two core delivery types (`CreateGameRole = 19`, `GameRoleFollow = 20`) handled through the Step 4 poller seam.

**A game's follow role.** A game has an optional Discord role (`RoleId`). Three ways to get one, all ending with the
same thing: the role id on the game and the role watched. (1) *Attach an existing role* (Discord role picker; web: paste
the role id). (2) *Create one in Discord* (the `/game add` wizard makes it itself). (3) *Create one from the website*:
the API marks the game `RoleState = Pending` and queues one `CreateGameRole`; the bot makes the role and reports back
(`PUT .../role-status`, bot only), which sets the role id or records a failure note. The note is shown on the website
("⚠️ I can't create roles here: my role needs Manage Roles"), and asking again clears it and retries.
Game roles and game-type roles join the watched set (`RoleWatchList`), so members' roles are snapshotted and the web
can show who follows what. A role can belong to only one game (409).

**Permission checks (the plan's warning requirement).** `GameRoleChecks` (pure): creating a role needs Manage Roles;
giving a role needs Manage Roles AND the bot's highest role above it, and never an integration-managed role or
@everyone. Each refusal names the fix ("drag my role above it in Server Settings → Roles"). The Discord wizard shows
the create warning at the role step and, if a pick is refused, says why and stays on the step instead of failing. The
website states what the bot needs before the role choice, and surfaces whatever the bot later reports. A Discord refusal
(403) is treated as a final answer, never retried. The bot also records a failure note when it cannot grant a role
to a follower, and clears it the next time a grant works.

**Following.** `POST games/{id}/follow` (member, for themselves) queues `GameRoleFollow`; the bot gives or takes the role
(same checks). `GET games/following` answers from the role snapshot, with the same fail-closed rule as signup
restrictions: if the snapshot is stale or a game role has not been checked yet it answers `Known = false` and the page
says it can't tell, rather than claiming "you don't follow". In Discord: `/game follow` and a public Follow button
(`/game panel`, or "Post a Follow button here" at the end of the wizard); both toggle the caller's role directly.

**Game types.** `/game type-add role label` and `/game type-remove role` (admins) and a "Game types" section on the
website (admins add by role id). They use the Step 3 `game-types` routes.

**Discord wizard.** `/game add` opens a form (name, fewest, most, usual table), then three steps with select menus: how a
result is recorded, the starting questions (players only / players and final scores), and the role (role-select menu to
attach, a "Create a ... role" button, or "No role"). Choices live in a short-lived in-memory draft keyed by a guid in
the component ids (15 minutes; only the person who started it can continue). The organiser check runs when the form is
submitted, because a modal must be the first response. The key is made from the name and kept unique
(`GameWizard.KeyFromName`). The result-type and template steps are two selects, and the template is deliberately small;
more questions are added on the game afterwards. Shared rules (`GameWizard.Build`) mean Discord and the website build the
same definition.

**Web.** A Games tab (the tabs component is now `GameNightsTabs`, showing Venues and Games when the feature is on),
a `/app/guilds/{id}/games` page: every member sees the library with a Follow button per game that has a role;
organisers add (four-step wizard), retire, create or attach a role; admins also apply the standard games file and manage
game types. The web cannot list a server's roles, so attaching is by pasted role id (the wizard says how to copy one).

**Verification.** Local: solution builds; Bot 476 and Web 189 tests pass. The Api tests (roles, follow, watched set) need
Docker and run in CI. **Not exercised against real Discord:** the role-select menu binding (`IRole[]`), role creation and
the hierarchy checks need a live server; try `/game add` once on the test server.

**Core touch points added this step.** `Contracts/Deliveries.cs` (two enum values), `Api/Services/RoleWatchList.cs`
(game and type roles), `Web/Components/GuildTabs.razor` (the tab component renamed), and the generated migration + snapshot.

---

# Step 6: game nights and teach visibility

**What landed.** Migration `GameNightsSignals` (additive): `GameNightSignals`, one row per member per game per night
(`can teach`, `can play`, `new to it`, `not tonight`). Key (night, member, game), so changing an answer updates the row
and there is never more than one. Cascades with the event; games are retired, never deleted.

**Arithmetic (pure, `SignalMath`).** Per game: counts of each answer; *keen* = teach + play + new (teachers play too);
*needs a teacher* = keen > 0 and nobody can teach it (passing on a game is not demand). People counts are distinct people:
*teachers* (can teach at least one game), *experienced* (can teach or play at least one), *fresh* (new to at least one),
*respondents* (any answer). A person can be both experienced and fresh. Every active game appears, busiest first.

**Routes** (`/guilds/{id}/game-nights/events/{e}/signals...`): `GET` (summary, member), `GET roster` (teachers per game,
member), `GET people` (everyone's answers, **organiser up**), `GET|PUT mine` (the caller's answers; PUT replaces them all),
`PUT {gameId}` (one answer; `Kind = null` clears it). The website always means the signed-in member; the bot names the
member (`UserId`). Hidden nights are not found. Answers are closed once the night is over, and only active games of the
server can be answered. People are shown as ids until Step 7's name service.

**Throttling the Discord post.** Every change queues a `SyncEventMessage` for the night **due one interval from now**
(`GameNights:EventPostIntervalSeconds`, default 20, 0 to 600), unless one is already waiting that the bot has not been served
yet (`Attempts == 0`): that one reads the data fresh when it runs, so it already covers the change. A row the bot has picked
up may have read the data before the change, so it doesn't count. Result: twenty answers in a window produce one edit, the
last answer in a window is always shown, and edits are at least an interval apart. Nights with no post queue nothing.

**On the event post (core touch points).** To show the summary and the button on *every* render of the post (RSVP clicks
re-render it too), the event DTO now carries an optional `GameNights` summary: `EventDto` gained a trailing nullable field,
the single-event DTO helper in `EventEndpoints` attaches it (only when the server has active games, so other servers'
posts are untouched), and `EventEmbedBuilder` calls three hooks in `GameNightsEmbed`: it holds back 700 characters of the
embed budget when there is a summary, adds a "🎲 Game night" field (answered, teaching, know their games, new, and the
busiest five games with "⚠️ no teacher yet" flags), and adds a **Pick games** button after the RSVP buttons (on the last row
if it has room, else a new row; left out if Discord's five rows are already full). A stress test pins the embed under
Discord's limits with the summary on. With no summary the post is exactly as before.

**Discord.** The Pick games button opens a private panel: four games a page, one select menu each (can teach / can play /
new to it / not tonight / clear), previous and next buttons. Each select saves immediately and redraws; there is no Save button.
**Web.** A "Games tonight" section on the event page: demand table (games nobody can teach highlighted, with a badge), the
teacher roster, the member's own picks (saved in one request), and for organisers everyone's answers. It refreshes itself every
15 seconds while open without touching unsaved picks.

**Not done.** The "experience list" is everyone's answers per person, not a separate seniority ranking. Nothing links answers to
Arrange tables (phase 2). Per-night counts are not retained beyond the event (they go with it).

**Verification.** Local: solution builds; Bot 492 and Web 196 tests pass (including a stress test of the embed limits and the
panel/limit checks). The Api tests (answers, replace, roster, visibility, throttle) need Docker and run in CI.
**Not exercised against real Discord:** the select panel and the edited post; try pressing Pick games on the test server.

**Core touch points added this step.** `Contracts/Events.cs` (`EventDto.GameNights`), `Api/Endpoints/EventEndpoints.cs` (one call in
the single-event DTO helper), `Bot/EventEmbedBuilder.cs` (budget, field, button hooks), `Web/Pages/App/EventDetail.razor`
(one `<EventGamesPanel>`), and the generated migration + snapshot.
