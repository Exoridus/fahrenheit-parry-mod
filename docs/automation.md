# Automation Overview

This repository uses `build.cmd` as the single local entrypoint.  
`build.cmd` runs NUKE targets implemented in `build/Build.cs`.

## Core Targets

- `build.cmd install`
  - Installs/checks prerequisites (`git`, `.NET SDK 10.x`).
  - With `--full`, also ensures `MSBuild` and `vcpkg`.
- `build.cmd setup`
  - Configures git hooks (`.githooks/commit-msg`).
  - Runs Fahrenheit setup restore.
  - Runs interactive auto-deploy setup flow.
- `build.cmd verify`
  - Validates commit parser self-test.
  - Builds mod (`Debug` by default).
  - Runs tests if matching `*test*.csproj` exist (excluding `.workspace`).

## Build Targets

- `build.cmd build`
  - Default local build target (`mod` by default).
- `build.cmd build --buildtarget mod`
  - Builds managed mod payload only.
- `build.cmd build --buildtarget full`
  - Builds full Fahrenheit payload (native + managed).
- `build.cmd buildrelease`
  - Full `Release` build.
  - Uses release pin (`fahrenheit.release.ref`) unless overridden with `--fahrenheitref`.

## Deploy Targets

- `build.cmd deploy --deploytarget mod|full --deploymode merge|replace --gamedir <path>`
  - Manual deployment into `GAME_DIR\fahrenheit\...`.
- `build.cmd setupautodeploy`
  - Interactive (or prefilled) setup for automatic local post-build deployment.

### Auto Deploy Modes

- `none` (default)
  - Automatic post-build deployment is disabled.
- `update`
  - Full builds deploy full payload with non-destructive merge copy.
- `replace`
  - Full builds replace destination before copying full payload.
- `mod-only`
  - Even full builds only deploy mod payload.

After a successful deployment, `.release/` is cleaned automatically.

## Release Targets

- `build.cmd releaseversion --bump patch|minor|major`
  - Bumps manifest version.
  - Regenerates `CHANGELOG.md`.
  - Pins Fahrenheit ref for release (`fahrenheit.release.ref`).
  - Creates release commit + annotated tag.
- `build.cmd releaseready`
  - Release preflight:
    - clean working tree check
    - commit format check for range
    - local verify
    - full release build
    - package dry-run artifacts
    - generate preview release notes
- `build.cmd packagerelease --tag vX.Y.Z`
  - Creates full + mod ZIP packages and SHA256 files.
- `build.cmd generatereleasenotes --tag vX.Y.Z --repository owner/repo`
  - Generates release notes markdown.

## Commit Targets

- `build.cmd commit`
  - Interactive Conventional Commit wizard.
- `build.cmd commit --committype feat --commitscope ui --commitmessage "add setting"`
  - Non-interactive commit creation.
- `build.cmd validatecommitmessage`
  - Validates single commit subject.
- `build.cmd validatecommitrange --range BASE..HEAD`
  - Validates all non-merge commit subjects in a range.
