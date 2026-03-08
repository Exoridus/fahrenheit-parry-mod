# AI Repository Rules — fahrenheit-parry-mod

These rules apply to all AI assistants working in this repository, including Claude, Codex, Gemini, and similar coding agents.

## 1) Project Purpose and High-Level Shape

This repository is a Final Fantasy X mod loaded through Fahrenheit.

It contains:
- runtime mod code in `src/`
- shipped runtime assets in `resources/`, `lang/`, and the runtime subset of `mappings/`
- build and automation code in `build/`
- contributor and operational documentation in `docs/`
- CI/CD and repository automation in `.github/`
- local development, research, extracted data, logs, tools, and generated workspace content in `.workspace/`

Treat `.workspace/` as development infrastructure, not as runtime architecture and not as the public source of truth.

## 2) Repository Structure Rules

Use these boundaries consistently:

- `src/` → runtime mod source code only
- `resources/` → shipped media and runtime assets
- `lang/` → shipped localization resources
- `mappings/` → mapping assets and mapping sources
- `build/` → build orchestration, automation, release, setup, and data workflows
- `tests/` → automated tests and validation helpers
- `docs/` → contributor-facing and operational documentation
- `.github/` → CI/CD and repository automation
- `.workspace/` → local-only workspace data, generated artifacts, external clones, research outputs, logs, tools, and machine-specific content

Do not move local research or temporary outputs into runtime or shipped paths.

## 3) Runtime vs Local Workspace Rules

Only a subset of the repository is runtime-critical and shipped.

Runtime-critical:
- `src/`
- `resources/`
- `lang/`
- runtime-consumed mapping bundles in `mappings/`

Local/development-only:
- `.workspace/`
- most research artifacts
- extracted raw datasets
- temporary outputs
- local notes
- machine-specific configuration

Do not treat `.workspace/` content as production-safe by default.
Do not make release logic depend on undocumented local-only workspace state unless explicitly intended and clearly documented.

## 4) Build and Workflow Contract

- Use `.\build.cmd` as the canonical command surface.
- Use `.\build.cmd help` and `.\build.cmd -h <workflow>` before introducing or changing workflow logic.
- Do not add parallel wrapper commands unless explicitly requested.
- Keep `verify` non-destructive.
- Prefer stable defaults and opt-in for risky behavior.
- Use Windows-style command examples in docs and assistant responses: `.\build.cmd ...`

## 5) Core Engineering Principles

- Prefer stable, reversible changes over broad speculative rewrites.
- Preserve runtime behavior unless behavior change is explicitly requested.
- Keep behavior deterministic where feasible.
- Use one clear control path instead of multiple hidden paths.
- Prefer minimal production surface area.
- Prefer narrow diffs and focused changes over repo-wide churn.
- State assumptions, tradeoffs, and risks explicitly.
- Mark speculative ideas as speculative.

## 6) Refactoring and Architecture Rules

- Prefer incremental restructuring over big-bang rewrites.
- Do not introduce new architectural layers unless they remove a concrete recurring pain.
- Do not split code into additional projects or assemblies without strong justification.
- Do not introduce dependency injection frameworks unless clearly justified.
- Do not add interfaces unless multiple implementations, test seams, or true boundary clarity require them.
- Avoid cargo-cult clean architecture and premature abstraction.
- Do not mix naming cleanup, architecture changes, and runtime behavior changes in one refactor unless explicitly requested.
- Keep PR-sized changes small, reviewable, and independently verifiable.
- When proposing a refactor, identify:
  - target boundary
  - touched files/areas
  - expected payoff
  - risk level
  - validation method
  - rollback path

## 7) Runtime Boundary Rules

Keep these concerns clearly separated inside runtime code:

- gameplay/parry/combat behavior
- Fahrenheit integration and hooks
- memory access foundations and offset definitions
- mappings and runtime lookup data
- settings and configuration
- overlays, diagnostics, and debug tooling
- session logging and observability

Avoid mixing low-level memory details directly into gameplay logic.
Avoid mixing experimental research code into active runtime paths.

## 8) Fahrenheit-First Integration Rules

- Prefer Fahrenheit helpers, hooks, enums, and abstractions before introducing custom low-level access.
- Reuse existing Fahrenheit functionality when available instead of duplicating wrappers.
- If custom offsets are required:
  - centralize them in `ExternalMemoryOffsetMap*`
  - keep typed access through dedicated memory registry/location types
  - avoid scattering raw addresses across gameplay code

## 9) Memory and Offset Rules

These concepts have distinct roles and should remain distinct:

- `MemoryLocation` → immutable descriptor of one memory target
- `MemoryRegistry` → named grouping of memory locations by domain
- `MemoryCandidates` → staging area for uncertain or externally mined offsets
- `ExternalMemoryOffsetMap*` → canonical raw offset definitions

Rules:
- keep uncertain offsets staged until validated
- do not present candidates as production-safe facts
- do not leak raw addresses into gameplay behavior code
- promote offsets from candidate/staging to trusted runtime usage only after explicit validation

## 10) Risky Changes Policy

Risky changes include anything touching:
- runtime hooks
- memory interaction
- startup flow
- low-level integration
- active combat behavior
- release packaging behavior

For risky changes:
- gate risky logic behind explicit opt-in where appropriate
- keep risky paths disabled by default unless explicitly requested otherwise
- define expected success and rollback conditions before implementation
- avoid repeated blind attempts
- when iteration is not converging, stop churn, return to a stable baseline, and report one clear next step

## 11) C#/.NET Style Rules

Prefer standard modern C#/.NET patterns that fit this repository:

- file-scoped namespaces
- nullable-aware code
- clear small methods with focused responsibility
- explicit state transitions over implicit side effects
- one primary type per file
- filename matches primary type
- related partials use `TypeName.<Domain>.cs`

Naming conventions:
- `PascalCase` for types, public/protected/internal methods, properties, and public constants (private methods use `snake_case` to match the existing codebase convention)
- `_camelCase` for private instance fields
- `camelCase` for locals and parameters

For touched code:
- resolve warnings introduced by the change
- remove dead fields, stale toggles, and temporary probe code after validation
- prefer concrete, readable implementations over clever indirection

## 12) Tests and Verification Rules

Use realistic verification, not fake ceremony.

Prefer automated checks for:
- deterministic logic
- config validation
- mapping validation
- schema validation
- package content assertions
- build/release assertions
- smoke checks
- golden-file tests where appropriate

Do not force artificial unit-test seams into tightly runtime-coupled code unless there is clear payoff.

After edits:
- run the smallest relevant validation first
- then run broader verification only if justified by the scope

## 13) CI/CD and Release Rules

- Conventional Commits are required.
- Keep release tags immutable; fix forward with a new version.
- Keep release outputs deterministic.
- Attach checksums when supported.
- Ensure required checks pass before merge or release.
- Add guardrails against accidentally shipping local, research, or generated workspace-only content.

## 14) Documentation Rules

- Keep docs aligned with real workflows and implemented parameters.
- Remove stale docs when behavior is removed or changed.
- Prefer concise operational docs over research-heavy narrative in public-facing files.
- Keep public docs focused on what contributors and users actually need.

## 15) Assistant Working Style Rules

- Prefer concrete edits over abstract recommendations.
- If blocked, report the exact blocker and the best fallback.
- If repository visibility is incomplete, say what is known, what is inferred, and what is conditional.
- Do not flatter.
- Do not recommend overengineering.
- Do not assume enterprise patterns are automatically appropriate for a modding project.
- Keep runtime logic separate from research artifacts and local-only infrastructure.

## 16) Default Priorities

Unless explicitly overridden:
1. stability and reproducibility
2. correctness and determinism
3. runtime safety
4. code clarity and maintainability
5. contributor friendliness
6. optional enhancements