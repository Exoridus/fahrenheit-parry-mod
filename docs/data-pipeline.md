# Data Pipeline Guide

The mod ships locale-specific mapping bundles under `mappings/runtime/` that
are loaded at runtime to resolve command / monster / battle / event display
text. The extraction and parsing of FFX game data that ultimately produces
that reference data is **no longer owned by this repo**. See
[REPO_BOUNDARY.md](../REPO_BOUNDARY.md) for the ownership split.

## Where the data lives now

All FFX game-data extraction, parsing, and analysis lives in the sibling
pipeline repo: `../ffx-knowledge-base`. That repo owns:

- Raw VBF extraction (`build.cmd data-extract`)
- FFXDataParser invocation (`build.cmd data-parse`, `data-parse-all`,
  `run-dataparser-commands`, `run-dataparser-scripts`)
- Canonical base and localized JSONs under
  `ffx-knowledge-base/output/ffx/game_data/`:
  `commands_base.json` + `commands_localized/<locale>.json`,
  `monsters_base.json` + `monsters_localized/<locale>.json`,
  `gear_abilities_base.json`, `items_base.json`, `key_items_base.json`,
  `monster_abilities_base.json`, `weapon_names.json`, etc.
- Crossrefs (`output/ffx/crossref/`), scripts (`output/ffx/scripts/`),
  community findings (`output/ffx/community/`), and packs (`packs/ffx/`).

See `ffx-knowledge-base/README.md` for the full workflow list and
example queries.

## Runtime mapping bundles (this repo)

This repo still owns the runtime bundle format under `mappings/runtime/`:

- `mappings/runtime/ffx-mappings.json` — US alias
- `mappings/runtime/ffx-mappings.{locale}.json` — per-locale bundle
  (`us`, `de`, `fr`, `it`, `sp`, `jp`, `ch`, `kr`)

These bundles combine commands, auto-abilities, key items, monsters, battles,
and events into a single loadable file per locale. They are shipped with the
mod via `Fahrenheit.Mods.Parry.csproj`:

```xml
<None Include="mappings/runtime/*.json" CopyToOutputDirectory="Always" />
```

And loaded at runtime by `ParryModule.DataMapping.cs` for display-name
resolution in the overlay and debug UI.

## Bundle regeneration

The canonical producer is `build.cmd build-mod-runtime-bundles` in the
sibling pipeline repo:

```
cd ../ffx-knowledge-base
build.cmd build-mod-runtime-bundles
# optionally: build.cmd build-mod-runtime-bundles --dry-run
```

The workflow reads exclusively from pipeline-owned canonical outputs and
writes `mappings/runtime/ffx-mappings.<locale>.json` for all 8 locales,
plus `ffx-mappings.json` (US alias) and `ffx-mappings.provenance.json`.

Input sources per runtime-bundle domain:

| Runtime domain  | Pipeline input                                           |
|-----------------|----------------------------------------------------------|
| `Commands`      | `output/ffx/game_data/items_localized/`, `commands_localized/`, `monster_abilities_localized/` |
| `AutoAbilities` | `output/ffx/game_data/gear_abilities_localized/`         |
| `KeyItems`      | `output/ffx/game_data/key_items_localized/`              |
| `Monsters`      | `output/ffx/game_data/monsters_localized/`               |
| `Battles`       | `canonical/ffx/scripts/text/<locale>/battles.json` (frozen)      |
| `Events`        | `canonical/ffx/scripts/text/<locale>/events.json`  (frozen)      |

The `Battles`/`Events` inputs are frozen snapshots committed in the
pipeline repo under `canonical/ffx/scripts/text/` pending a future
canonical script-text extractor.

Do not hand-edit `mappings/runtime/` directly. Run the generator instead.

The old `mappings/source/` tree that previously lived in this repo was
deleted once the pipeline producer moved onto canonical outputs.

## Notes

- Data extraction/parsing is not required to build or run the mod; the
  runtime bundles in `mappings/runtime/` are sufficient.
- Mod releases consume the pre-generated JSON bundles; no re-parsing is
  performed at build time.
