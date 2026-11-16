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
