# Development Guide

## Prerequisites

Required (all local development):
- Windows
- Git
- .NET SDK 10.x (pinned by `global.json`)

Required for native Fahrenheit builds:
- Visual Studio Build Tools (or Visual Studio) with:
  - `.NET desktop development`
  - `Desktop development with C++`
- `vcpkg integrate install`

Optional (data workflows only):
- Java 21+
- Maven (`mvn`)

Optional (reverse engineering workflows):
- Ghidra (managed via `.\tools.cmd ghidra-setup`)

## Quick Start

```bash
.\build.cmd install --full
.\build.cmd setup
.\build.cmd doctor
.\build.cmd verify -c Debug --verbosity quiet
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

Verbosity defaults to `normal`.
- Explicit level: `--verbosity|-v quiet|minimal|normal|detailed|diagnostic`
- Escalation path: `quiet` -> `normal` -> `detailed` -> `diagnostic`
- Agent guidance: use `--verbosity quiet` for routine `.\build.cmd verify`, `.\build.cmd build`, and `.\build.cmd deploy` runs.
- Common shorthand: `-c <config-path>`, `-n` for `--dry-run`, `-h` for help.

Quality:

```bash
.\build.cmd format
.\build.cmd doctor [--full]
.\build.cmd lint [--target Debug|Release] [--config .\path\to\config.local.json]
.\build.cmd smoke [--target Debug|Release] [--config .\path\to\config.local.json]
.\build.cmd verify [--target Debug|Release] [--config .\path\to\config.local.json] [--repo owner/repo]
.\build.cmd clean [--full]
```

Build and deploy:

```bash
.\build.cmd build [--target Debug|Release] [--config .\path\to\config.local.json] [--auto-deploy|--no-auto-deploy]
.\build.cmd deploy [--game-dir "C:\\Path\\To\\Game"] [--target Debug|Release] [--config .\path\to\config.local.json]
.\build.cmd auto-deploy [--game-dir "C:\\Path\\To\\Game"]
.\build.cmd start [--game-dir "C:\\Path\\To\\Game"] [--elevated|--no-elevated]
```

Reverse engineering tooling:

```bash
.\tools.cmd help
.\tools.cmd ghidra-setup
.\tools.cmd ghidra-start
.\tools.cmd discord-sync --guild 612363389003366405
```

## Parry Timing Determinism Checks

```bash
dotnet test tests/Parry.Tests/Parry.Tests.csproj -c Debug
```

The test suite includes simulation-time pacing checks for 1x/2x/4x-equivalent deltas and variable frame pacing.

## Local Config

Local build and tooling settings are stored in `.workspace/config.local.json`.

Schema keys are strict and case-sensitive:

- `BuildTarget` (`Debug` or `Release`)
- `InstallPath` (game install directory containing `FFX.exe`)
- `AutoDeploy` (`true`/`false`/`null`; `null` prompts once in interactive mode)
- `PreservePaths` (array of deploy-preserved paths under `<InstallPath>\fahrenheit` and/or absolute paths)
- `OpenApiUrl`
- `OpenApiKey`
- `OpenApiModel`
- `FetchRetryCount`
- `DiscordToken`

`.\build.cmd build` can override deploy behavior per run:

- `--auto-deploy` forces deploy for this run even if `AutoDeploy` is `false`.
- `--no-auto-deploy` disables deploy for this run even if `AutoDeploy` is `true`.

Build and deploy workflows always operate on full payloads to reduce stale artifact issues.
Deploy behavior mirrors artifact state into the selected deploy target and preserves entries from `PreservePaths`.

Use `--dry-run` with `build` or `deploy` to preview sync actions without writing files.

`.\tools.cmd discord-sync` reads Discord auth and enrichment settings from `.workspace/config.local.json` and workflow-level sync filters from `.workspace/discord/config.local.json`.

Recommended shape for `.workspace/config.local.json`:

```json
{
  "BuildTarget": "Release",
  "InstallPath": "",
  "AutoDeploy": null,
  "PreservePaths": ["mods/loadorder", "saves"],
  "OpenApiUrl": "",
  "OpenApiKey": "",
  "OpenApiModel": "",
  "FetchRetryCount": 2,
  "DiscordToken": ""
}
```

Recommended shape for `.workspace/discord/config.local.json`:

```json
{
  "Blacklist": [
    "123456789012345678"
  ],
  "Guilds": [
    "612363389003366405"
  ]
}
```

Config keys are strict and case-sensitive.

The Discord sync workflow exports immutable raw channel JSON into `.workspace/discord`, discovers channels and threads automatically, and defaults to full-or-delta behavior per channel. Voice channels, thread discovery, media downloads, media reuse, and advisory rate-limit handling are always enabled for consistency. Assets are stored per guild at `<Guild Root>\\Media`. OCR outputs are sidecars (`*.ocr.txt`) and the Tesseract pre-pass only runs on larger images (currently max dimension >= 640px). Fetched text/code is stored as `*.src.txt`, per-channel reference indexes are stored as `*.refs.jsonl`, and per-server metadata is written to `server.refs.json`. A local `Blacklist` array can exclude known inaccessible or unsupported channel/thread IDs before sync starts, and future inaccessible IDs are persisted back into that same local config automatically. Use `.\tools.cmd discord-sync --guild <serverId> --full` periodically to reconcile edits/deletions that delta mode cannot recover.

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
- Local config schema: `docs/workspace-config.local.schema.json`


