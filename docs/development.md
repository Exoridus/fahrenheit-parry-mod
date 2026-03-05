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

Quality:

```bash
.\build.cmd doctor [--full]
.\build.cmd lint [--config Debug|Release]
.\build.cmd smoke [--payload mod|full] [--config Debug|Release]
.\build.cmd verify [--config Debug|Release] [--repo owner/repo]
```

Build and deploy:

```bash
.\build.cmd build [--payload mod|full] [--config Debug|Release]
.\build.cmd deploy [--payload mod|full] [--mode merge] [--gamedir "C:\\Path\\To\\Game"]
.\build.cmd auto-deploy [--gamedir "C:\\Path\\To\\Game"] [--mode none|update|mod-only]
.\build.cmd start [--gamedir "C:\\Path\\To\\Game"] [--elevated]
```

Reverse engineering tooling:

```bash
.\build.cmd ghidra-setup
.\build.cmd ghidra-start
```

## Parry Timing Determinism Checks

```bash
dotnet test tests/Parry.Tests/Parry.Tests.csproj -c Debug
```

The test suite includes simulation-time pacing checks for 1x/2x/4x-equivalent deltas and variable frame pacing.

## Local Config

`.\build.cmd auto-deploy` stores settings in `.workspace/dev.local.json`:

- `GAME_DIR`
- `DEPLOY_MODE` (`none`, `update`, `mod-only`)

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
  - `verify` matrix for `Debug` and `Release`
- tag push `v*`: `.github/workflows/release.yml`
  - full release build
  - release packaging (`full` and `mod-only` ZIP)
  - SHA256 outputs
  - generated release notes

## References

- Workflow map: `docs/automation.md`
- Data pipeline: `docs/data-pipeline.md`
- Local config schema: `docs/dev-local.schema.json`
