# Development Guide

## Prerequisites

Required (all local development):
- Windows
- Git
- .NET SDK 10.x (pinned by `global.json`)

Required for full native builds (`--payload full`):
- Visual Studio Build Tools (or Visual Studio) with:
  - `.NET desktop development`
  - `Desktop development with C++`
- `vcpkg integrate install`

Optional (data workflows only):
- Java 21+
- Maven (`mvn`)

Optional (reverse engineering workflows):
- Ghidra (managed via `.\build.cmd ghidra-setup`)

## Quick Start

```bash
.\build.cmd install --full
.\build.cmd setup
.\build.cmd doctor
.\build.cmd verify --config Debug
```

## Core Workflows

Discover workflows:

```bash
.\build.cmd help
.\build.cmd -h <workflow>
```

Bool flags use this style:
- `--flag` to enable
- `--no-flag` to disable

Quality:

```bash
.\build.cmd format
.\build.cmd doctor [--full]
.\build.cmd lint [--config Debug|Release]
.\build.cmd smoke [--payload mod|full] [--config Debug|Release]
.\build.cmd verify [--config Debug|Release] [--repo owner/repo]
.\build.cmd clean [--full]
```

Build and deploy:

```bash
.\build.cmd build [--payload mod|full] [--config Debug|Release]
.\build.cmd deploy [--payload mod|full] [--game-dir "C:\\Path\\To\\Game"]
.\build.cmd auto-deploy [--game-dir "C:\\Path\\To\\Game"]
.\build.cmd start [--game-dir "C:\\Path\\To\\Game"] [--elevated|--no-elevated]
```

Reverse engineering tooling:

```bash
.\build.cmd ghidra-setup
.\build.cmd ghidra-start
.\build.cmd discord-sync --guild 612363389003366405
```

## Parry Timing Determinism Checks

```bash
dotnet test tests/Parry.Tests/Parry.Tests.csproj -c Debug
```

The test suite includes simulation-time pacing checks for 1x/2x/4x-equivalent deltas and variable frame pacing.

## Local Config

`.\build.cmd auto-deploy` stores settings in `.workspace/dev.local.json`:

- `GameDir`
- `AutoDeploy` (`true`/`false`/`null`, default `null`) -> global default for auto-deploy in `build` workflow. If `null`, the first local build asks once and stores the choice.
- `DeployBlocklist` (array of paths to preserve/skip during deploy; supports relative paths under `GameDir\fahrenheit` and absolute paths; default `["mods/loadorder", "saves"]`).

`.\build.cmd build` can override `AutoDeploy` per run:

- `--deploy` forces deploy for this run even if `AutoDeploy` is `false`.
- `--no-deploy` disables deploy for this run even if `AutoDeploy` is `true`.

Auto-deploy target follows the build target:
- `--payload mod` -> deploy `mod`
- `--payload full` -> deploy `full`

Deploy behavior mirrors artifact state into the selected deploy target and preserves entries from `DeployBlocklist`.

Use `--dry-run` with `build` or `deploy` to preview sync actions without writing files.

`.\build.cmd discord-sync` reads a Discord token from this local-only file:

- `.workspace/discord/config.local.json`

Recommended shape for `.workspace/discord/config.local.json`:

```json
{
  "token": "...",
  "defaults": {
    "includeVc": true,
    "includeThreads": "All",
    "media": true,
    "reuseMedia": true,
    "respectRateLimits": true
  },
  "blacklist": [
    "123456789012345678"
  ]
}
```

The Discord sync workflow exports JSON into `.workspace/discord`, discovers channels and threads automatically, and defaults to full-or-delta behavior per channel. Voice channels are included by default because they can also contain text chat. If you do not set `defaults.mediaDir`, the build workflow now resolves a server-local media directory at `<Guild Root>\\Media` and keeps each guild's downloaded assets there by default. A local `blacklist` array can exclude known inaccessible or unsupported channel/thread IDs before sync starts, and future inaccessible IDs are persisted back into that same local config automatically. Use `.\build.cmd discord-sync --guild <serverId> --full` periodically to reconcile edits/deletions that delta mode cannot recover.

## Commit Workflows

```bash
.\build.cmd commit
.\build.cmd commit --type feat --scope ui --subject "add timeline row grouping"
.\build.cmd commit-check --message "feat: add timeline row grouping"
.\build.cmd commit-range --range origin/main..HEAD
```

## Release Workflows

```bash
.\build.cmd release-ready --repo Exoridus/fahrenheit-parry-mod
.\build.cmd release-bump --bump patch --repo Exoridus/fahrenheit-parry-mod
git push origin main --follow-tags
```

`release-bump` updates version/changelog, pins `fahrenheit.release.ref`, creates release commit, and creates annotated tag.

## CI/CD Summary

- `push`/`pull_request` to `main`: `.github/workflows/ci.yml`
  - commit subject validation (PR)
  - `Verify` job (`Debug`) + `Verify (Release)` job
- tag push `v*`: `.github/workflows/release.yml`
  - full release build
  - release packaging (`full` and `mod-only` ZIP)
  - SHA256 outputs
  - generated release notes

## References

- Workflow map: `docs/automation.md`
- Data pipeline: `docs/data-pipeline.md`
- Localization strategy: `docs/localization.md`
- Pointer/hook primer: `docs/pointers-hooks-guide.md`
- Local config schema: `docs/dev-local.schema.json`


