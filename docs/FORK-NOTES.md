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
| `CreateEventRequest` + `EventEndpoints.CreateEvent` | three optional **bot-only** fields: `PostViaOutbox`, `InitialAttendeeIds`, `NativeEventId` (event import; ignored for web callers, defaults change nothing) | import |

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

---

# Step 7: result reporting, backend

**What landed.** Migration `GameNightsResults` (additive): `GameNightResults`, `GameNightResultPlayers` (a member by Discord
id OR a guest; a check constraint makes it exactly one), `GameNightResultValues` (table-wide answers),
`GameNightResultConfirmations`, `GameNightGuests`, `GameNightMemberNames`, plus the foreign key on table seats' `GuestId`
that Step 4 left open. A new core delivery type, `ResultDisputed = 21`, handled through the poller seam.

**Names: the decision, and how it differs from the plan.** The plan said member names are not stored. The owner has said names
are fine to keep (people give names for tournaments anyway), so the rule now is: **only the derived "First L" is ever
stored, only for people who take part, never a nickname, username or anything else from Discord.**
- The API cannot read Discord (ADR 0001/0004), so the bot works "First L" out of each participant's server nickname
  (`DisplayNames.FromNickname`, case-insensitive: "alex pearson" gives "Alex P"; hyphens and apostrophes handled; "Anne de
  Smith" gives "Anne S") and pushes **only that**. With no server nickname it may use the member's own chosen global
  display name, never the username. A single word, digits, emoji or a pipe and a place do not match and are pushed as null.
  The API itself refuses to store anything that is not exactly the derived form, so a raw nickname cannot get in however
  the bot changes (tested).
- `MemberNameSyncService` (bot, every 10 minutes, only when Game Nights is on) asks the API who it needs names for (people at
  tables, in results or answering, with no name or a stale one: a matched name is refreshed after 12 hours, an unmatched one
  after an hour so a fixed nickname is picked up quickly), looks them up, and pushes. A member who is gone is reported as left.
- `NameService` is the one place a name is decided: left gives "Former member"; a stored name; a one-off name typed for a
  single report (used for that response only and never stored, validated as First L); otherwise "Unnamed player", and the
  member is listed in the response's `UnnamedUserIds` so the bot can nudge them to fix their nickname (the nudge itself is
  Step 8, in the report flow). Everyone in the server can read these names (they are what Discord already shows); guests are
  shown by their typed name.
- **Guests** (people with no Discord account): organisers add them (`First L` only), they can appear in results, and a
  moderator can erase one on request: the name becomes "Former guest" and the row stays so results keep their shape.
- The privacy test checks the schema: no column in any Game Nights table is named photo, image, url, link, blob, file, nick,
  username or avatar, and the only name columns are the guest's typed name and the derived member name.
- **Needs the privacy page updated** before this is switched on for prod: Game Nights keeps a member's "First L" (derived
  from their server nickname) and a Discord id against results. Owner agreed this is fine; the text is not written yet.

**Reporting and confirming.** `POST tables/{t}/results` (a member who played, or an organiser; the bot names the reporter),
checked by the Step 3 validator against the table's game (player count, placements, required answers, pick lists, unique
answers). Members are stored by id only; the score is the answer to the question keyed `score`; other answers are kept as
small JSON, table-wide ones as rows. **No photo field or link exists anywhere**: the board photo stays in Discord and never
reaches the API. A table has at most one result that is pending, confirmed or disputed at a time; a voided one frees it.
- *Pending → Confirmed* when enough of the members who played have confirmed. The reporter counts as confirming if they
  played; guests cannot confirm; a result with no members (only guests) is confirmed at once.
  **The rule is configurable: `GameNights:ConfirmMode` = `All` (default) or `Majority` (more than half).** This is the open
  question from the plan; I defaulted to All with a moderator override. Confirmed marks the table Played.
- *Rejected* by any player → Disputed, and **one** `ResultDisputed` is queued (not again on further rejections) naming the
  roles mapped to organiser, moderator or admin; the bot posts it in the table's thread (or the night's channel), pinging
  only the rejector and those roles. A confirm cannot override a dispute.
- A **moderator** can confirm a pending or disputed result, or void any result; voiding a confirmed one puts the table back to
  Open so it can report again.
- Reporting is allowed for any table that is not cancelled, whatever state the night is in.

**Routes** (`/guilds/{id}/game-nights/...`): `GET|POST tables/{t}/results`, `GET results/{r}`, `POST results/{r}/confirm|reject`
(members who played; the bot names them), `POST results/{r}/resolve` (moderator); `PUT member-names`,
`GET member-names/wanted` (bot only), `GET names?ids=` (member); `GET|POST guests` (organiser), `DELETE guests/{g}`
(moderator, erases the name).

**Not done.** The Discord report flow, photo prompt, reminders and the nickname nudge are Step 8. The web still shows people
as "user {id}"; wiring the name routes into every page is Step 10. League effects are Step 9 (only confirmed results count).

**Verification.** Local: solution builds; Bot 500 tests pass. The Api tests (lifecycle, validation, who may report or answer,
names, guests, the schema privacy check, the majority rule) need Docker and run in CI.

**Core touch points added this step.** `Contracts/Deliveries.cs` (one enum value), and the generated migration + snapshot.

---

# Step 8: result reporting, Discord flow

**What landed.** Migration `GameNightsReportFlow` (additive, two columns): `GameNightGames.PhotoRequired` (a yes/no setting)
and `GameNightResults.SummaryMessageId` (the Discord message the bot posted for the result). Three more core delivery types:
`ResultSubmitted = 22`, `ResultReminder = 23`, `ResultSettled = 24`.

**The flow.** Every table thread gets a **Report game** button (also `/table report`), for games Discord can handle (a player
list, at most 25 players, pick-lists of at most 25 values; bigger games are reported on the website). Pressing it opens a
private (ephemeral) multi-step message, built from the game's own question list, so a new game needs no new code:
1. **Players** (a user select, with the seated players already chosen and a "use the seated" button).
2. **Places**: per-player selects, three to a page (Discord's five rows: three selects, page buttons, step buttons); or "who won?"
   for single-winner games; or a side per player for winning-side games.
3. **Required questions first** (per-player pick-lists such as faction/colour as paged selects; table-wide pick-lists as one
   select; typed answers such as scores in a **form, five inputs at a time**, which is Discord's limit, so six players need two
   forms), **then the optional ones, each skippable**.
4. **Photo**, then **Summary** with Submit and Edit. Submit is switched off while anything would be refused (the shared validator
   plus the photo rule), and Edit goes back to the places keeping every answer.
All the rules live in `ReportFlow` (plain operations on a `ReportSession`); the Discord module only translates interactions into
calls and redraws. In memory for 20 minutes, only the person who started a report can continue it.

**The photo.** The step asks the reporter to post a photo of the final board in the thread. The bot **never downloads or stores
it and the API never sees it**: no photo or link is part of any request (tested). The reporter may skip it, unless an admin has
made it required for that game (`/game photo-required game required`, or the game's definition): then Skip is refused and Submit
stays off until the reporter presses "I've posted it". **That is the reporter's word, not a check**: without the message-content
intent the bot cannot see attachments, and we do not want that intent just for this.

**After Submit, everything the bot says goes through the outbox**, queued in the same transaction as the result: a
`ResultSubmitted` post in the table's thread (summary, **Confirm** and **Reject** buttons, pinging only the members who still
have to answer, plus a nudge to anyone with no usable "First L" nickname), and, while it waits, one `ResultReminder` after
`GameNights:ResultReminderHours` (default 24, 0 switches reminders off) that does nothing if everyone has answered by then. When it
settles (the last confirmation, or a moderator confirming or voiding) a `ResultSettled` line is posted and the original post's
buttons are removed (the bot records the post's message id with the API, and takes the post back if that fails so a retry cannot
double-post). Confirm and Reject answer the API as the member who pressed them. Rejection and its organiser notice are Step 7's.

**Tests.** The flow is scripted end to end, with no Discord, against the *real* Root and Arcs definitions (the games file is
embedded into the bot tests): every move, the next screen, and the submission the shared validator accepts; required before
optional; forms of five; places for each result type; photo skip works and the required flag blocks; and a sweep rendering every
step and page of both games with 2 to 6 players and 90-character names to check the row, option, custom-id and form limits.

**Not done.** Moderators settle a dispute through the API (and the website in Step 10); there is no `/result` command. Guests
cannot be added in the Discord flow (website, Step 10). The nickname nudge is a note in the result post, not a DM.
**Not exercised against real Discord:** the user-select, the form and the buttons; try Report game on the test server.

**Core touch points added this step.** `Contracts/Deliveries.cs` (three enum values), the generated migration + snapshot.

---

# Step 9: leagues and ratings

**What landed.** Migration `GameNightsLeagues` (additive): `GameNightLeagues` (one per game, open-ended, made the first time a rated result
counts), `GameNightSeasons`, `GameNightPlayerRatings`, `GameNightRatingEvents` (the league entries: before and after for every player of every
counted game), and an `InLeague` flag on `GameNightTables` and `GameNightEventExtensions`.

**The rating.** `PairwiseElo` behind `IRatingScheme` (Contracts, so the scheme can be swapped later in one place, `LeagueEngine.Scheme`):
`E(i,j) = 1/(1+10^((Rj-Ri)/400))`, `S` = 1 / 0.5 / 0, `ΔRi = K/(N-1) × Σ(S - E)`. Start 1000 and K 32, both per game. Checked against hand-worked
examples: two equal players move 16 (ordinary Elo); 1000 beating 1200 gains 24.3119; four equal players finishing 1-2-3-4 move +16, +5.33, -5.33,
-16; ties between equals change nothing; and the total across a game is always conserved. **Winner-only games** (decided here, as the plan left it
open): the winner is rated against everyone and the others are treated as **tied with each other**, so they neither gain nor lose from one another.
Winning-side games tie players on the same side. Ratings are stored exactly and shown rounded.

**What counts.** A result counts when it is **confirmed**, its game has ratings on, its table is **public** and its night is **public**. Nothing else
ever reaches a league. **The database enforces the public rule**: tables and nights carry `InLeague`, with check constraints
`Visibility = 0 OR NOT InLeague`, so making a counted table or night non-public fails in the database whatever any code does (tested for both). When a
result is voided the flag clears and the table can be made private again.

**How ratings are produced.** By **replaying** every counted result of the game in order (night, then when it was reported) through the scheme, inside
the request's transaction and under a lock on the game. One way to get a rating, so **voiding or correcting a confirmed result is just a rebuild**
(tested: voiding the middle of three games gives exactly the ratings of the other two, in order; voiding all empties the table). It runs when a result
becomes confirmed (the last confirmation, an immediate confirmation, a moderator's confirm), when a confirmed one is voided, when a season starts or
ends, and on demand (moderator). Players join by playing: a player's first counted result creates their rating at the game's start rating.
Hundreds or thousands of results replay in milliseconds; if a league ever outgrew that, the engine is the one place to make it incremental.

**Seasons.** Optional. Each result is rated into the all-time rating AND into the season its night falls in, if any; a season starts everyone fresh,
so the all-time rating carries on while the season starts at 1000 (tested: Autumn and Winter each show 1016 for one game; all-time shows 1030.53).
Starting a season ends the one running at that moment; overlaps are refused. The table shown by default is the running season, else all-time.

**Routes** (`/guilds/{id}/game-nights/...`): `GET games/{g}/league[?seasonId|allTime]` (member), `GET games/{g}/league/history[?userId|guestId]`,
`GET leagues/me[?userId]`, `POST games/{g}/league/seasons`, `POST .../seasons/{s}/end` (organiser), `POST .../recompute` (moderator).
**Bot:** `/league standings game [all-time]` and `/league rating`. Names are the First L ones (guests by their typed name).

**Retention.** The purge only touches the tables it lists, so results, players, ratings, rating events and leagues are exempt by omission. A test now
pins it: league data is built, the purge runs with a clock 800 days ahead, and every count is unchanged.

**Not done.** The website's league pages are Step 10. Per-game K and start rating are in the game definition but have no editor yet beyond editing the
game. Rating a game that is later switched to "ratings off" empties its league (the results stay).

**Verification.** Local: solution builds; Bot 535 tests and the 13 pure Elo tests pass. The Api league tests (the headline three-game standings, voids,
seasons, the database constraints, retention) need Docker and run in CI.

**Core touch points added this step.** The generated migration + snapshot only.


---

# Step 10: role-aware website

**Decision that differs from the plan: no photo upload on the website.** The plan had the report form upload the board photo to the API, which
would forward it to the thread through the bot and discard it. That cannot be done without breaking a rule: the API never calls Discord (ADR
0001/0004), so anything for the bot goes through the delivery outbox, which is a database table (rows live 90 days). A photo queued there would sit in
the database, against "nothing is written to disk or the database". Three ways out were put to the owner (hold the bytes in server memory and let
the bot fetch them; no upload; a short-lived database blob). **The owner chose no upload.** The report form therefore has no file field; it tells
the reporter to post the photo in the table's thread (with a link), exactly as the Discord flow does ("the reporter's word", Step 8), and the game's
"photo required" setting is shown as a note. The plan's "no residue" test is replaced by two that pin the same promise: no report contract has a
property that could carry a photo, link or file, and the report page renders no file input and sends no photo field.

**Pages** (all under the guild, in `CalCrony.Web/GameNights/`, no core page edited beyond the seams below):
- **Leagues** (`/app/guilds/{id}/leagues`): game picker, current / all-time / per-season table, names linked to the player page. Organisers get the
  season controls (start now, end the running one); moderators also get "Rebuild ratings".
- **Results** (`/results`): recent results (filter by game, "only mine"), a "Waiting for you" strip for results the viewer still has to answer, the
  guest list for organisers and "Erase name" for moderators. Members see none of the guest controls and the page does not even ask for the list.
- **Player profile** (`/players/{userId}`): name, rating per game with rank, a small rating line, the history table (before, after, change) and recent results.
- **Report a result** (`/tables/{id}/report`), linked from the table page: built from the game's own question list (ranked place, single winner or winning side;
  pick-lists, numbers and text, per player or per table), seated players ticked by default, guests added by organisers. The shared `ResultValidator` runs on every
  keystroke so the button stays off while anything would be refused; `ReportDraft` holds the form state and produces the same `ResultSubmission` the bot sends
  (single-winner games send only the winner's place, as the bot does). A member who is not in the list and is not an organiser is told they cannot report; the API
  enforces it either way.
- **Result card**: place, name and score, extra answers, status; Confirm and Reject for a player who still has to answer, Accept as reported and Void for a moderator;
  a nickname nudge for a player with no usable "First L".
- **Table page**: "Report a result" link and the table's results.
- Tabs gain **Leagues** and **Results**. Every member sees every pill; pages show members the read-only view (the API decides what is allowed).

**Names.** Every page now shows people through the name service: `GameNightsWebApiClient.GetNamesAsync` asks `GET names` in batches of 100 and remembers answers for
the visit, and anyone it cannot name shows as "Unnamed player". The table card (seats, host picker) and the games panel (teachers, everyone's answers) no longer show
`user {id}`.

**One API addition:** `GET /guilds/{id}/game-nights/results?gameId&userId&status&take` (member tier, newest first, take capped at 50): the result history page needs a
guild-wide list. Each row passes the same table-visibility check as reading it directly, so a result at a table the caller cannot see never appears (tested, with the filters and
that another server's results never show). No schema change.

**Also fixed.** Step 9's dev run was red: the rating history ordered two games at the same night by a random event id. It now orders by night, then when the result was
submitted, then result id, the same order the engine replays in (PR #15).

**Tests.** `ResultsWebComponentTests` (draft rules; the report form with and without organiser powers, the guard on a member who did not play, the cancelled table, the
submit request; result cards for each tier; history, guests and erase; leagues and seasons per tier; the profile; the table page; tabs). The Api test for the new route needs Docker and runs in CI.

**Not exercised against real Discord or a browser.** The pages were checked through component tests only; open Leagues, Results and a table's report form on the test stack.

**Core touch points added this step.** None: everything is in the extension's folders.


---

# Step 11: hardening and handover

**Endpoint review, made permanent.** Every Game Nights route was listed with its tier and what filters its rows. The review found no route without a
tier; the gap was that nothing could *check* that, because `RequireTier` is an endpoint filter and leaves no trace on the route. It now also records a
`RequiredTier` metadata item (`TierGuard.cs`), and `GameNightsRouteAuditTests` pins **every Game Nights route** to its tier: a route added without one, a pinned route
that disappears, or a tier that is loosened or tightened fails the test and shows in the diff. It also pins the five bot-only routes (role status, table thread,
result post id, member names and the wanted-names list) and that `GET /me` is the only route open to every signed-in caller. Row filters, checked by reading: tables,
results and the new results list go through `CanSeeTableAsync` per row; signals and the event venue go through `CanSeeEventAsync`; the league routes only ever see
public, confirmed data (the database constraint guarantees it). The `/probe/*` routes are leftovers from Step 1 used by the tier tests; they leak nothing and stay.

**Load check, and a lock.** `GameNightsLoadApiTests` sends **300 answers from 300 different people at once** (40 in flight) to one night with a posted message, and asserts every
answer is stored, the summary counts 300 respondents and **exactly one** held-back `SyncEventMessage` is queued, due about an interval out; a second wave after the bot has
picked up the first row queues exactly one more. The throttle did a check-then-insert with no lock, so two answers arriving together could both find nothing waiting and each
queue an edit. Both answer routes now take a per-night Postgres advisory transaction lock (`EventPostThrottle.LockNightAsync`, keyed on the event, released on commit) around
"apply the answer, queue the edit if none is waiting, save". Different nights never wait for each other. I did not see the race fail before adding the lock (it depends on
timing); the test is what stops it coming back.

**Privacy, verified in the data and not only the schema.** Two new tests. One creates a guest ("yolanda zxqv", stored as "Yolanda Z"), tries to push a raw nickname for a
member (refused with 400), reports a result with the guest, then searches **every text and JSON column of every table** for the guest's first name, the nickname and the
surnames behind the initials: the raw nickname and surnames are found nowhere, the guest's name is found while the guest exists and **nowhere at all after a moderator erases it**
(the result then reads "Former guest"). The other asserts no Game Nights table has a binary (`bytea`) column, so a photo could not be written even by mistake. The guest erase route
already existed (`DELETE guests/{id}`, moderator). The Step 7 schema test (no photo, link, nickname or username columns) still holds.

**README and notes.** README has a Game Nights section (flag, every setting with its default, the role-to-tier mapping with a working `curl`, game types, the games file, the bot
permissions and the intents it does and does not use, and what is stored about people). `docs/PHASE-2-NOTES.md` lists what is intentionally missing (arranger, polls, tournaments,
check-in, help guide, private-game UI, moderator queue and admin screens, TI4, other rating schemes) with the hook left for each, plus **four things to do before prod** that are not
phase 2: the privacy paragraph, a walk-through on the test stack, a screen for the role-to-tier mapping, and the invite permissions.

**Verification.** A local .NET 10 SDK is at `C:\Users\alexp\dotnet10-sdk` (not on PATH; the plan said there was none): the solution builds locally. Docker was not running, so the Api tests (including the three new files' worth) run in CI only.

**Core touch points added this step.** None.

**Upstream sync (PR #18, cut from `dev`).** `upstream/master` was three commits ahead of the mirror (60 files): the release-notes tooling (`tools/ReleaseNotes`) and its
tests (`tests/CalCrony.ReleaseNotes.Tests`), tighter permissions on upstream's two release jobs, and docs (`aidlc-docs`, `docs/release-notes`, a README line). **The merge had no
conflicts and touched none of the files in the Game Nights touch-point table**, so no touch point moved. Checked locally on top of Steps 10 and 11: the solution builds; Bot 535,
Web 225 and the new ReleaseNotes tests 344 pass (17 skipped, Windows symlink checks); the Api tests need Docker and run in CI. `master` (the mirror) was not advanced: that
push is the owner's, per HOME.md. The sync PR should be merged with a merge commit so upstream history is kept.
