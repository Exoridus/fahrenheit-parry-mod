# Contributing

## Scope

- This repository is Windows-first for build/deploy workflows.
- Keep automation changes in `build.cmd`, `build/`, and `.github/workflows/`.
- Keep gameplay/runtime changes inside `src/`.

## Prerequisites

- Git
- .NET SDK 10.x
- For full builds: Visual Studio Build Tools + `vcpkg integrate install`

Install prerequisites:

```cmd
.\build.cmd install --full
```

## First-Time Setup

```cmd
.\build.cmd setup
.\build.cmd doctor
.\build.cmd verify --config Debug
```

`setup` configures local git hooks and Fahrenheit workspace setup.

## Daily Workflow

```cmd
.\build.cmd lint --config Debug
.\build.cmd verify --config Debug
.\build.cmd build --payload mod --config Debug
.\build.cmd deploy --payload mod --mode merge
```

Optional cleanup:

```cmd
.\build.cmd clean
.\build.cmd clean --full
```

## Commit Conventions

- Use Conventional Commits:
  - `feat: ...`
  - `fix: ...`
  - `docs: ...`
  - `refactor: ...`
  - `build: ...`
  - `ci: ...`
  - `chore: ...`
- Keep each commit focused (one concern per commit where possible).
- Do not mix broad refactors with behavior changes unless inseparable.
- Local `commit-msg` hook blocks invalid commit subjects.
- CI validates commit subjects in PRs.

Helpful command:

```cmd
.\build.cmd commit
.\build.cmd commit --type feat --scope ui --subject "add queue row grouping"
```

## PR Expectations

- Run `.\build.cmd verify --config Debug` before opening/updating PRs.
- Include concise testing notes in the PR description:
  - build config used
  - whether in-game validation was performed
  - any known limitations
- Keep PRs small and reviewable when possible.
- Update docs when command names, workflows, or user-facing behavior change.

## Release Flow

1. Ensure `main` is clean and up to date.
2. Run:

```cmd
.\build.cmd release-ready --repo Exoridus/fahrenheit-parry-mod
.\build.cmd release-bump --bump patch --repo Exoridus/fahrenheit-parry-mod
git push origin main --follow-tags
```

`release-bump` updates version/changelog, pins `fahrenheit.release.ref`, creates the release commit, and creates an annotated tag.
