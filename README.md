<div align="center">

<!----><a name="top"></a>

# Fahrenheit Parry Mod

[![Latest](https://img.shields.io/github/v/release/Exoridus/fahrenheit-parry-mod?style=for-the-badge&label=Latest&logo=github&color=44cc11)](https://github.com/Exoridus/fahrenheit-parry-mod/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Exoridus/fahrenheit-parry-mod/total?style=for-the-badge&label=Downloads&logo=github)](https://github.com/Exoridus/fahrenheit-parry-mod/releases/latest)
[![CI](https://img.shields.io/github/actions/workflow/status/Exoridus/fahrenheit-parry-mod/ci.yml?branch=main&style=for-the-badge&label=CI&logo=githubactions)](https://github.com/Exoridus/fahrenheit-parry-mod/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/actions/workflow/status/Exoridus/fahrenheit-parry-mod/release.yml?style=for-the-badge&label=Release&logo=githubactions)](https://github.com/Exoridus/fahrenheit-parry-mod/actions/workflows/release.yml)
[![License](https://img.shields.io/github/license/Exoridus/fahrenheit-parry-mod?style=for-the-badge&label=License)](LICENSE)
[![Sponsor](https://img.shields.io/badge/Sponsor-1a1e23?style=for-the-badge&logo=githubsponsors)](https://github.com/sponsors/Exoridus)

![Banner](resources/Banner.png)

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
- [x] Setup/bootstrap via `make setup`
- [x] Canonical argument-based build/deploy commands
- [x] Local version bump + changelog generation (`make release-version`)
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
- GNU Make

For full native builds (`BUILD_TARGET=full`):

- Visual Studio Build Tools (or Visual Studio) with:
  - `.NET desktop development`
  - `Desktop development with C++`
- `vcpkg integrate install`

### Quick Start

Install `make` first (required):

```cmd
:: This should print a GNU Make version at the end.
(where make >NUL 2>&1 || winget install --id "ezwinports.make" -e --source winget --accept-source-agreements --accept-package-agreements) && make --version
```

After that:

```bash
make install
make setup
make verify
make deploy GAME_DIR="C:\Path\To\Game"
```

`make setup` also installs local git hooks (`core.hooksPath=.githooks`) so non-conventional commit messages are blocked before commit creation.

## Build / Deploy Commands

```bash
# Build mod (default)
make build
make build BUILD_TARGET=mod CONFIGURATION=Debug

# Verify scripts + build + tests (if present)
make verify CONFIGURATION=Debug

# Create a conventional commit (default type: chore, no scope)
make commit COMMIT_MSG="update docs"
make commit COMMIT_TYPE=feat COMMIT_SCOPE=ui COMMIT_MSG="add timing mode toggle"

# Full build (native + managed)
make build BUILD_TARGET=full CONFIGURATION=Debug

# Full release build
make build BUILD_TARGET=full CONFIGURATION=Release

# Deploy mod output (default)
make deploy GAME_DIR="C:\Path\To\Game"

# Deploy full output (merge mode)
make deploy DEPLOY_TARGET=full DEPLOY_MODE=merge GAME_DIR="C:\Path\To\Game"

# Deploy full output (replace/mirror mode)
make deploy DEPLOY_TARGET=full DEPLOY_MODE=replace GAME_DIR="C:\Path\To\Game"
```

Important overrides:

- `CONFIGURATION=Debug|Release`
- `GAME_DIR=<path to folder containing FFX.exe>`
- `BUILD_TARGET=mod|full`
- `DEPLOY_TARGET=mod|full`
- `DEPLOY_MODE=merge|replace`

## Release Flow (Maintainers)

```bash
# 1) bump version, regenerate changelog, create release commit + annotated tag
make release-version BUMP=patch

# 2) push commit and tag
git push origin main --follow-tags
```

`BUMP` supports: `patch`, `minor`, `major`.

## CI/CD

- `push`/`pull_request` to `main`: `.github/workflows/ci.yml`
  - validates commit message format on pull requests
  - runs `make verify`
- tag push `v*`: `.github/workflows/release.yml`
  - builds full release output
  - packages release assets
  - emits SHA256 checksum files for both ZIP assets
  - generates release notes via `scripts/generate-release-notes.cmd`
  - publishes GitHub release assets

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
