# Contributing

## Scope

- This repository is Windows-first for build/deploy workflows.
- Keep automation changes in `build.cmd`, `build/`, and `.github/workflows/`.

## Prerequisites

- Git
- .NET SDK 10.x
- For full builds: Visual Studio Build Tools + `vcpkg integrate install`

Install prerequisites:

```cmd
build.cmd install --full
```

## Local Verification

Run the standard local verification gate before pushing:

```cmd
build.cmd verify --configuration Debug
```

Run setup once per clone to install local git hooks:

```cmd
build.cmd setup
```

Configure or update automatic local deployment:

```cmd
build.cmd setupautodeploy
```

## Commit Style

- Use Conventional Commits where possible:
  - `feat: ...`
  - `fix: ...`
  - `docs: ...`
  - `chore: ...`
  - `ci: ...`
- Keep changes focused and avoid unrelated formatting churn.
- Local `commit-msg` hook blocks invalid formats.
- CI also validates PR commit subjects.

Optional helper for creating conventional commit messages:

```cmd
build.cmd commit --commitmessage "update docs"
build.cmd commit --committype feat --commitscope ui --commitmessage "add toggle"
```

## Release Flow

1. Ensure `main` is clean and up to date.
2. Run:

```cmd
build.cmd releaseversion --bump patch
```

This command updates version/changelog and writes `fahrenheit.release.ref` with the exact Fahrenheit commit hash that release builds must use.

3. Push commit and tag:

```cmd
git push origin main --follow-tags
```

Tag pushes (`v*`) trigger `.github/workflows/release.yml`, which builds, packages, generates checksums/release notes, and publishes assets.
