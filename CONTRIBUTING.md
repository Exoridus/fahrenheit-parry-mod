# Contributing

## Scope

- This repository is Windows-first for build/deploy workflows.
- Keep automation changes in `Makefile`, `scripts/`, and `.github/workflows/`.

## Prerequisites

- Git
- .NET SDK 10.x
- GNU Make
- For full builds: Visual Studio Build Tools + `vcpkg integrate install`

Install prerequisites:

```cmd
make install
```

## Local Verification

Run the standard local verification gate before pushing:

```cmd
make verify CONFIGURATION=Debug
```

Run setup once per clone to install local git hooks:

```cmd
make setup
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
make commit COMMIT_MSG="update docs"
make commit COMMIT_TYPE=feat COMMIT_SCOPE=ui COMMIT_MSG="add toggle"
```

## Release Flow

1. Ensure `main` is clean and up to date.
2. Run:

```cmd
make release-version BUMP=patch
```

3. Push commit and tag:

```cmd
git push origin main --follow-tags
```

Tag pushes (`v*`) trigger `.github/workflows/release.yml`, which builds, packages, generates checksums/release notes, and publishes assets.
