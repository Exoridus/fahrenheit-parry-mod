# AI Engineering Rules for `fahrenheit-parry-mod`

This file defines project rules for AI assistants.  
Treat these as defaults unless the maintainer explicitly overrides them.

## 1) Core Principles

- Prefer stable, reversible changes over broad speculative rewrites.
- Keep behavior deterministic and testable.
- Use one clear control path instead of multiple hidden paths.
- Default to minimal surface area in production code.

## 2) Workflow Contract

- Use `.\build.cmd` as the canonical command surface.
- Use `.\build.cmd help` and `.\build.cmd -h <workflow>` before adding new workflow logic.
- Do not add parallel wrapper commands unless requested.
- Keep `verify` non-destructive (no implicit deploy side effects).

## 3) Change Strategy

### 3.1 Routine changes
- Keep commits focused to one concern.
- Update docs and help text in the same change if behavior changed.
- Preserve existing user-facing defaults unless an explicit change is requested.

### 3.2 Risky changes
Risky means anything that touches runtime hooks, memory interaction, startup flow, or low-level behavior.

- Gate risky logic behind explicit opt-in flags.
- Keep risky paths disabled by default.
- Define expected success and rollback conditions before implementation.
- Revert failed experiments quickly and keep the stable path clean.

### 3.3 Trial limits
- Avoid repeated blind attempts.
- After a small number of failed variants without new signal, switch to targeted instrumentation or rollback.

## 4) Language Rules

- Use English only for code, comments, docs, logs, and commit messages.
- Use Windows command examples in docs: `.\build.cmd ...`.

## 5) C#/.NET Code Style Rules

- Prefer standard modern C#/.NET patterns:
  - file-scoped namespaces,
  - nullable-aware code,
  - clear small methods with single responsibility,
  - explicit state transitions over implicit side effects.
- Follow naming conventions:
  - `PascalCase` for types/methods/properties/constants exposed in APIs,
  - `_camelCase` for private instance fields,
  - `camelCase` for local variables/parameters.
- File naming convention:
  - one primary type per file,
  - filename matches type name (`TypeName.cs`),
  - related partials use `TypeName.<Domain>.cs`.
- For touched code, resolve warnings introduced by the change.
- Remove dead fields, stale toggles, and temporary probe code after validation.

## 6) Fahrenheit-First Integration Rules

- Prefer Fahrenheit helpers/functions/hooks/enums before introducing custom low-level access.
- Reuse existing Fahrenheit abstractions when available instead of duplicating wrappers.
- If custom offsets are required:
  - centralize them in `ExternalMemoryOffsetMap`,
  - keep typed access through dedicated memory registry/location types,
  - avoid scattering raw addresses across gameplay code.

## 7) Repository Structure Rules

- `build/`: build orchestration and automation source.
- `src/`: mod runtime source code.
- `resources/`: external media/config assets shipped with the mod.
- `tests/`: automated tests.
- `docs/`: contributor and operational documentation.
- `lang/`: localization resources.
- `mappings/`: runtime/import mapping assets.
- `.github/`: CI/CD and repository automation.
- `.workspace/`: local/generated workspace data (not public source of truth).

## 8) Runtime Safety Rules

- Do not ship aggressive behavior changes enabled by default.
- Keep debug/diagnostic features from degrading normal runtime UX.
- Avoid focus/input side effects unless explicitly requested.
- Keep fallback behavior simple and predictable.

## 9) Data and Artifacts Rules

- Keep tracked repository content lean and intentional.
- Keep large/generated/transient data in workspace-oriented locations unless explicitly versioned.
- Distinguish validated findings from candidates in code and docs.
- Do not present uncertain offsets/hooks as production-safe facts.

## 10) CI/CD and Release Rules

- Conventional Commits are required.
- Keep release tags immutable; fix forward with a new version.
- Ensure required checks pass before merge/release.
- Keep release outputs deterministic and attach checksums when supported.

## 11) Documentation Rules

- Keep command docs aligned with actual implemented workflows and parameters.
- Remove stale docs for removed behavior.
- Prefer concise operational docs over research-heavy narrative in public-facing files.

## 12) Assistant Response Rules

- State assumptions and tradeoffs explicitly.
- Prefer concrete edits over abstract recommendations.
- If blocked, report the exact blocker and best fallback.
- Mark speculative ideas as speculative.
- When iteration is not converging, stop churn and return to a stable baseline with one clear next step.

## 13) Default Priorities

Unless overridden by the maintainer:
1. stability and reproducibility
2. correctness and determinism
3. code clarity and maintainability
4. optional enhancements
