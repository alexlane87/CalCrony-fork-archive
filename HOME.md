# Fork notes (alexlane87/CalCrony)

This is a fork of [JJWren/CalCrony](https://github.com/JJWren/CalCrony) run on a Raspberry Pi 5
(`alex-pi-1`). Deployment config lives in the private `alex-pi-1` repo under `stack/calcrony/`,
not here.

## Branches

| Branch | Purpose |
|---|---|
| `master` | Clean mirror of upstream `master`. Fast-forward only, never commit here. |
| `home` (default) | `master` plus our changes: `.github/workflows/arm64-images.yml`, this file, any customisations. |

## Syncing from upstream

```bash
git fetch upstream --tags
git checkout master && git merge --ff-only upstream/master && git push origin master
git checkout home && git merge master && git push origin home
```

## Customisations on `home`

Keep this list short: every entry is a potential merge conflict on upstream sync.

- `src/CalCrony.Web/Pages/Privacy.razor` and `Terms.razor`: operator is **West Country Leders**
  (not the upstream maintainer), contact is the community's mod team, "last updated" dates bumped.
  The sections describing what the software stores are untouched, so they stay correct. After an
  upstream sync, re-read upstream's version of both pages and carry any new data-handling
  statements across. The test in `ComponentTests.cs` only pins the Limited Use and available-hours
  wording. `PRIVACY_POLICY.md` and `TERMS_OF_SERVICE.md` at the repo root are upstream's and are not used by our deployment.
- Known leftover: the footer's "discord" link (`MainLayout.razor`) points at upstream's community server
  and a test pins that URL.

## Building images for the Pi

Upstream publishes amd64 images only. Build arm64 images (pushed to `ghcr.io/alexlane87/calcrony-*`):

```bash
gh workflow run arm64-images.yml -f ref=v0.37.0 -f version=0.37.0
```

The upstream workflows (`ci`, `codeql`, `release-please`) are **disabled** in this fork so they don't
open release PRs or push amd64 images. Re-enable in the Actions tab if needed.

Customised builds (our legal pages etc.) come from the `home` branch, not an upstream tag:

```bash
gh workflow run arm64-images.yml -f ref=home -f version=0.37.0-wcl1
```
