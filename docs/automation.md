# Automation Overview

`build.cmd` is the project lifecycle entrypoint.

_Auto-generated from build workflow metadata. Do not edit manually; run `.\build.cmd docs-sync`._

Quick command discovery:
- `.\build.cmd help`
- `.\build.cmd -h <workflow>`
- Bool parameters support both `--flag` and `--no-flag`.
- Local research/tooling workflows moved to `tools.cmd` (`tools.cmd help`).

## Core Workflows

- `.\build.cmd install`
  - Install/check local prerequisites.
  - Parameters:
  - --full (optional).
  - --dry-run (optional).
  - Examples:
  - `.\build.cmd install`
  - `.\build.cmd install --full`

- `.\build.cmd setup`
  - Prepare repository for local development.
  - Examples:
  - `.\build.cmd setup`

- `.\build.cmd clean`
  - Default clean removes cache + build artifacts. Add explicit flags for analysis/exports/game-data/tools or use --purge for full local cleanup.
  - Parameters:
  - --analysis (optional).
  - --exports (optional).
  - --game-data (optional).
  - --tools (optional).
  - --purge (optional, requires --yes).
  - --yes (required with --purge).
  - --dry-run (optional).
  - Examples:
  - `.\build.cmd clean`
  - `.\build.cmd clean --analysis`
  - `.\build.cmd clean --exports --game-data`
  - `.\build.cmd clean --purge --yes`

- `.\build.cmd auto-deploy`
  - Configure automatic post-build deployment.
  - Parameters:
  - --game-dir <path> (optional).
  - --refresh-game-dir (optional).
  - Examples:
  - `.\build.cmd auto-deploy`
  - `.\build.cmd auto-deploy --game-dir "C:\Games\Final Fantasy X-X2 - HD Remaster"`

- `.\build.cmd doctor`
  - Diagnose local toolchain and environment state.
  - Parameters:
  - --full (optional).
  - Examples:
  - `.\build.cmd doctor`
  - `.\build.cmd doctor --full`

- `.\build.cmd format`
  - Apply code formatting/style fixes using dotnet format.
  - Examples:
  - `.\build.cmd format`

- `.\build.cmd lint`
  - Run fast lint/compile checks for build, mod, and tests projects.
  - Parameters:
  - --target Debug|Release (optional).
  - --config <path-to-config.local.json> (optional).
  - Examples:
  - `.\build.cmd lint`

- `.\build.cmd smoke`
  - Run quick sanity checks against a full build.
  - Parameters:
  - --target Debug|Release (optional).
  - --config <path-to-config.local.json> (optional).
  - Examples:
  - `.\build.cmd smoke`

- `.\build.cmd verify`
  - Run local validation without deploy side effects.
  - Parameters:
  - --target Debug|Release (optional).
  - --config <path-to-config.local.json> (optional).
  - --repo owner/repo (optional).
  - Examples:
  - `.\build.cmd verify`

- `.\build.cmd build`
  - Build full Fahrenheit payload.
  - Parameters:
  - --target Debug|Release (optional).
  - --config <path-to-config.local.json> (optional).
  - --auto-deploy or --no-auto-deploy (optional).
  - --dry-run (optional).
  - Examples:
  - `.\build.cmd build`
  - `.\build.cmd build --target Release`

- `.\build.cmd deploy`
  - Deploy full build artifacts into InstallPath.
  - Parameters:
  - --game-dir <path> (optional).
  - --refresh-game-dir (optional).
  - --target Debug|Release (optional).
  - --config <path-to-config.local.json> (optional).
  - --dry-run (optional).
  - Examples:
  - `.\build.cmd deploy`

- `.\build.cmd start`
  - Launch the game via deployed Fahrenheit stage0 loader.
  - Parameters:
  - --game-dir <path> (optional).
  - --refresh-game-dir (optional).
  - --elevated (optional).
  - Examples:
  - `.\build.cmd start`

## Release Workflows

- `.\build.cmd release-bump`
  - Bump version and create release commit/tag.
  - Parameters:
  - --bump patch|minor|major (optional).
  - Examples:
  - `.\build.cmd release-bump`

- `.\build.cmd release-ready`
  - Run release preflight.
  - Parameters:
  - --target Debug|Release (optional).
  - --config <path-to-config.local.json> (optional).
  - --repo owner/repo (optional).
  - Examples:
  - `.\build.cmd release-ready`

- `.\build.cmd release-pack`
  - Package built release payloads into ZIP archives.
  - Parameters:
  - --tag vX.Y.Z (required).
  - --deploy-dir <path> (optional).
  - --out-dir <path> (optional).
  - Examples:
  - `.\build.cmd release-pack --tag v0.0.1`

- `.\build.cmd release-notes`
  - Generate release-notes markdown/text for a tag.
  - Parameters:
  - --tag vX.Y.Z (required).
  - --repo owner/repo (required).
  - --out <path> (optional).
  - Examples:
  - `.\build.cmd release-notes --tag v0.0.1 --repo owner/repo`

## Commit Workflows

- `.\build.cmd commit`
  - Create a Conventional Commit.
  - Parameters:
  - --type feat|fix|... (optional).
  - --scope <scope> (optional).
  - --subject "message" (required in non-interactive mode).
  - --breaking (optional).
  - Examples:
  - `.\build.cmd commit`

- `.\build.cmd commit-check`
  - Validate one commit message.
  - Parameters:
  - --commit-file <path> or --message "...".
  - Examples:
  - `.\build.cmd commit-check --message "feat: x"`

- `.\build.cmd commit-range`
  - Validate commit subjects in a git range.
  - Parameters:
  - --range <BASE..HEAD> (required).
  - Examples:
  - `.\build.cmd commit-range --range origin/main..HEAD`

## Utility Workflows

- `.\build.cmd docs-sync`
  - Regenerate docs/automation.md and docs/tools-automation.md from workflow metadata.
  - Examples:
  - `.\build.cmd docs-sync`

