<div align="center">

<!----><a name="top"></a>
# Fahrenheit Parry Mod
[![Latest](https://img.shields.io/github/v/release/Exoridus/fahrenheit-parry-mod?style=for-the-badge&label=Latest&logo=github&color=44cc11)](https://github.com/Exoridus/fahrenheit-parry-mod/releases/latest)
[![Release](https://img.shields.io/github/actions/workflow/status/Exoridus/fahrenheit-parry-mod/release.yml?style=for-the-badge&label=Release&logo=githubactions&color=44cc11)](https://github.com/Exoridus/fahrenheit-parry-mod/actions/workflows/release.yml)
![Banner](https://github.com/Exoridus/fahrenheit-parry-mod/blob/main/resources/Banner.png?raw=true)
<!--[![Sponsor](https://img.shields.io/badge/Sponsor-1a1e23?style=for-the-badge&logo=githubsponsors)](https://github.com/sponsors/Exoridus)-->
<!--[![Downloads](https://img.shields.io/github/downloads/Exoridus/fahrenheit-parry-mod/total?style=for-the-badge&label=Downloads&logo=github)](https://github.com/Exoridus/fahrenheit-parry-mod/releases/latest)-->

</div>

## Description

`fahrenheit-parry-mod` is a standalone mod project for the Fahrenheit framework that adds timing-based parry gameplay to **Final Fantasy X / X-2 HD Remaster**.

Goals:

- detect enemy attack cues in live battle state
- open/resolve a configurable parry window
- process player parry input (`R1`)
- trigger optional feedback/effects on success

## Features

### Implemented

- [x] Standalone repo workflow (not requiring in-tree Fahrenheit mod sources)
- [x] Setup/bootstrap via `build.cmd setup`
- [x] Canonical argument-based build/deploy commands
- [x] Local version bump + changelog generation (`build.cmd releaseversion`)
- [x] Split CI/release workflows (`ci.yml`, `release.yml`)
- [x] Automated tag release notes generation

### Planned

- [ ] Optional conventional-commit lint gate in CI
- [ ] Optional smoke tests when a stable harness is available
- [ ] Release checksum/signature metadata

## End-User Install

Download release assets from the latest GitHub release:

- Full package: `fahrenheit-full-<tag>.zip` (recommended)
- Mod-only package: `fhparry-mod-<tag>.zip` (for existing Fahrenheit installs)
- SHA256 files: `*.zip.sha256` (integrity verification)

### Full Package (recommended)

1. Open your game install directory (contains `FFX.exe`).
2. Extract `fahrenheit-full-<tag>.zip` there.
3. Launch through your normal Fahrenheit loader flow.

### Mod-Only Package

1. Ensure Fahrenheit is already installed.
2. Extract `fhparry-mod-<tag>.zip` into `GAME_DIR/fahrenheit/mods/`.
3. Ensure `GAME_DIR/fahrenheit/mods/loadorder` contains `fhparry`.

## Contributor Setup

### Prerequisites

- Windows
- Git
- .NET SDK `10.x`
- SDK pinning via `global.json` (`10.0.100`, roll-forward within feature band)

For full native builds (`--buildtarget full`):

- Visual Studio Build Tools (or Visual Studio) with:
  - `.NET desktop development`
  - `Desktop development with C++`
- `vcpkg integrate install`

### Quick Start

`build.cmd` is the single entrypoint. It checks `.NET SDK 10.x` and prompts to install it with `winget` when missing.

After that:

```bash
build.cmd install --full
build.cmd setup
build.cmd verify
build.cmd deploy --gamedir "C:\Path\To\Game"
```

`build.cmd setup` installs local git hooks and then configures automatic local build deployment.
You can configure or reconfigure deployment later with:

```bash
build.cmd setupautodeploy
```

Prefill options for no-prompt setup:

```bash
build.cmd setupautodeploy --gamedir "C:\Path\To\Game" --autodeploymode update
```

Config is stored in `.workspace/dev.local.json` using:
- `GAME_DIR`
- `DEPLOY_MODE` (`none`, `update`, `replace`, `mod-only`)

## Build / Deploy Commands

```bash
# Build mod (default)
build.cmd build
build.cmd build --buildtarget mod --configuration Debug

# Verify scripts + build + tests (if present)
build.cmd verify --configuration Debug

# Configure automatic local deployment
build.cmd setupautodeploy

# Release preflight checks
build.cmd releaseready --repository "Exoridus/fahrenheit-parry-mod"

# Create a conventional commit (default type: chore, no scope)
build.cmd commit
build.cmd commit --committype feat --commitscope ui --commitmessage "add timing mode toggle"

# Full build (native + managed)
build.cmd build --buildtarget full --configuration Debug

# Full release build
build.cmd buildrelease

# Deploy mod output (default)
build.cmd deploy --gamedir "C:\Path\To\Game"

# Deploy full output (merge mode)
build.cmd deploy --deploytarget full --deploymode merge --gamedir "C:\Path\To\Game"

# Deploy full output (replace/mirror mode)
build.cmd deploy --deploytarget full --deploymode replace --gamedir "C:\Path\To\Game"
```

Important overrides:

- `--configuration Debug|Release`
- `--gamedir <path to folder containing FFX.exe>`
- `--buildtarget mod|full`
- `--deploytarget mod|full`
- `--deploymode merge|replace`
- `--fahrenheitref <git ref>` (optional override; default local builds track `origin/main`)
- `--autodeploymode none|update|replace|mod-only` (setup/build override)

## Release Flow (Maintainers)

```bash
# 1) bump version, regenerate changelog, create release commit + annotated tag
build.cmd releaseversion --bump patch

# 2) push commit and tag
git push origin main --follow-tags
```

`BUMP` supports: `patch`, `minor`, `major`.

`build.cmd releaseversion` also pins the exact Fahrenheit commit used for release builds into `fahrenheit.release.ref`.  
`build.cmd buildrelease` consumes that pinned ref for deterministic release artifacts.

Recommended before `releaseversion`:

```bash
build.cmd releaseready --repository "Exoridus/fahrenheit-parry-mod"
```

## CI/CD

- `push`/`pull_request` to `main`: `.github/workflows/ci.yml`
  - validates commit message format on pull requests
  - runs `build.cmd verify`
- tag push `v*`: `.github/workflows/release.yml`
  - builds full release output
  - packages release assets
  - emits SHA256 checksum files for both ZIP assets
  - generates release notes via `build.cmd generatereleasenotes`
  - publishes GitHub release assets
- Dependabot:
  - update PRs configured by `.github/dependabot.yml`
  - patch/minor dependency PRs are auto-set to squash auto-merge by `.github/workflows/dependabot-automerge.yml`
  - merge happens only when branch protection checks are satisfied

## Automation Docs

- Target and workflow map: `docs/automation.md`
- Local config schema: `docs/dev-local.schema.json`

## GUI Settings

Open Fahrenheit mod config (`F7`) and configure `Parry Prototype`.

- Enable Parry
- Timing Mode (`Legacy window` / `Clamp on hit`)
- Parry Window (seconds)
- Resolve Window Cap (seconds)
- Show Visual Indicator
- Play Audio Feedback
- Boost Overdrive Gauge
- Debug Logging
- Negate Damage
- Physical Lead Delay
- Magic Lead Delay
- Future Features (placeholder)

## Limitations

- Enemy/attacker classification still has heuristic fallback logic.
- Damage negation currently relies on observed runtime damage registers.
- FFX-2-specific tuning is not finished yet.

## Assets

Project image assets are stored in `resources/`.

- `resources/Banner.png`
- `resources/Logo.png`

---

**[Back to Top](#top)**
