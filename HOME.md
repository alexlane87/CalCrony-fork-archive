# Fork notes (alexlane87/CalCrony)

This is a fork of [JJWren/CalCrony](https://github.com/JJWren/CalCrony) run on a Raspberry Pi 5
(`alex-pi-1`). Deployment config lives in the private `alex-pi-1` repo under `stack/calcrony/`,
not here.

## Branches and environments

Three tiers, each a step closer to the members:

| Tier | Branch | Runs on | Purpose |
|---|---|---|---|
| 1. local dev | `dev` (default) | your PC, `docker-compose.local.yml` | Integration branch. All work lands here first, by PR. Nothing is published or deployed from it. Own Discord app and server. |
| 2. staging | `staging` | **staging** stack on the Pi (LAN only) | A real deployed copy to check a change on the Pi's hardware, with the test Discord app and server. Takes PRs from `dev`, or from a `promote/*` branch holding a hand-picked subset of `dev`. |
| 3. prod | `prod` | **prod** stack on the Pi (West Country Leders) | What is live. Only takes PRs from `staging`. |
| | `master` | nothing | Clean mirror of upstream `master`. Fast-forward only, never commit here. |

`dev`, `staging` and `prod` are protected: changes go in by pull request, the `build-test` and
`pr-source` checks must pass, and force-pushes and deletion are blocked. `master` only blocks
force-pushes and deletion. `dev` can be ahead of `staging`, and `staging` ahead of `prod`: a feature
(Game Nights is the first) stays on `dev` until you choose to promote it.

## Local dev stack

```powershell
tools/local-stack.ps1 up        # builds from your working tree: web :8082, api :8080, postgres :5433
tools/local-stack.ps1 logs api
tools/local-stack.ps1 reset     # delete the local database
```

The first `up` creates `.env.local` from [`.env.local.example`](.env.local.example). Without Discord
credentials the web app, API and database run and the bot idles, which is enough for website work.
For the bot and sign-in, create a separate **dev** Discord application and server, put its values in
`.env.local`, and add `http://localhost:8080/auth/discord/callback` as an OAuth redirect. Never reuse
the staging or prod bot token: two running copies of one bot both answer every command. Game Nights
is switched on locally (`GAMENIGHTS_ENABLED`). Needs Docker Desktop running.

## Shipping a change

```bash
git checkout dev && git pull
git checkout -b fix/poll-picker
# ...commit, try it with tools/local-stack.ps1 up...
gh pr create --base dev            # CI runs; squash-merge when green
```

1. **Local.** Merging to `dev` runs the build and tests only.
2. **Staging.** `gh pr create --base staging --head dev --title "promote: <what>"`, merged with a
   **merge commit** (not squash, so the branches keep a shared history). That builds arm64 images
   (`X.Y.Z-stg.N`) and moves the `:staging` tag. The Pi notices within a couple of minutes, deploys
   the staging stack, and posts start/finish messages in the staging server's deploy channel. Try it
   in the staging Discord server.
   To promote only *some* of what is on `dev`: branch from `staging` as `promote/<what>`,
   `git cherry-pick` the commits, and open the PR from that branch.
3. **Prod.** `gh pr create --base prod --head staging --title "release: <what>"`, merged with a
   merge commit. That builds `X.Y.Z-wcl.N`, moves `:prod`, and the Pi backs up, deploys and
   health-checks prod, rolling back to the previous images if the new ones do not come up healthy.

Everything is in `.github/workflows/wcl-pipeline.yml`. The Pi side (deploy script, timers,
notifications, rollback) is documented in `alex-pi-1` under `stack/calcrony/README.md`.

Database migrations run when the API starts. A rollback puts the old images back but does not
undo a migration; the pre-deploy backup is the way back from a bad schema change.

## Syncing from upstream

Upstream changes go through `dev`, then staging, like anything else:

```bash
git fetch upstream --tags
git checkout master && git merge --ff-only upstream/master && git push origin master --tags
git checkout dev && git pull && git checkout -b sync/upstream-vX.Y.Z
git merge master                   # resolve conflicts with the customisations below
gh pr create --base dev            # merge with a MERGE COMMIT so the upstream history is kept
```

The version prefix comes from the newest upstream `v*` tag reachable from the branch, so push tags.

## Customisations

Keep this list short: every entry is a potential merge conflict on upstream sync.

- `.github/workflows/wcl-pipeline.yml` and this file.
- **West Country Leaders theme** (`wcl`): a sixth interface theme using the community website's
  palette, Quicksand font and wooden-plank header. It is the default for new users and devices
  (upstream's default is `slate`, dark face; ours is `wcl`, auto face following the device). Touches `InterfaceThemes.cs`,
  `theme-init.js`, `site.js`, `index.html`, `theme.css`, `fonts.css`, `NavMenu.razor`,
  `ThemeToggle.razor`, `InterfaceThemePicker.razor` and the font file
  `wwwroot/fonts/quicksand-latin-400-700.woff2`. The theme list lives in both C# and JS, so keep
  them in step after an upstream sync.
- **Straight to events**: `/` sends signed-in visitors to `/app`, and `/app` opens the server's
  events when the user is in exactly one CalCrony server (`/app?all=1` always shows the list; the
  sidebar and the pages' back links use it). Touches `Landing.razor`, `Guilds.razor` and the guild
  pages' back links.
- `src/CalCrony.Web/Pages/Privacy.razor` and `Terms.razor`: operator is **West Country Leders**
  (not the upstream maintainer), contact is the community's mod team, "last updated" dates bumped.
  The sections describing what the software stores are untouched, so they stay correct. After an
  upstream sync, re-read upstream's version of both pages and carry any new data-handling
  statements across. The test in `ComponentTests.cs` only pins the Limited Use and available-hours
  wording. `PRIVACY_POLICY.md` and `TERMS_OF_SERVICE.md` at the repo root are upstream's and are not used by our deployment.
- Known leftover: the footer's "discord" link (`MainLayout.razor`) points at upstream's community server
  and a test pins that URL.

## Upstream workflows

Upstream publishes amd64 images only, so we build our own arm64 ones. The upstream workflows
(`ci`, `codeql`, `release-please`) are **disabled** in this fork so they don't open release PRs or
push amd64 images. Leave them disabled; `wcl-pipeline` replaces them here.
