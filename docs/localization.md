# Localization Strategy

This project uses two localization layers with different purposes:

- `lang/*.json`
  - UI labels/descriptions for the mod settings panel.
  - Consumed by Fahrenheit mod localization loading.
- `mappings/runtime/ffx-mappings.<locale>.json`
  - Game data mappings (commands, monsters, battles, events, etc.).
  - Consumed by this mod's runtime mapping loader.

## Canonical Locale IDs

Canonical IDs used by mapping workflows:

- `us`, `de`, `fr`, `it`, `sp`, `jp`, `ch`, `kr`

## Language File Policy

`lang/` uses one file per language with the same language IDs Fahrenheit expects:

- `en-US.json` (framework-style English)
- `de-DE.json` (framework-style German)

Current localized content is complete for English and German. Additional locales can be added incrementally by creating matching `lang/<FahrenheitLangId>.json` files.

## Runtime Mapping Fallback

At runtime, mapping lookups are locale-aware:

1. Try preferred locale bundle (for example `ffx-mappings.de.json`)
2. Fall back to `ffx-mappings.us.json`
3. Fall back to `ffx-mappings.json` (US compatibility alias)

## Build/Data Pipeline

The legacy `map-import` / `map-build` workflow (previously `build/Build.Data.cs`)
was removed as part of the pipeline-vs-mod boundary cleanup. The canonical
FFX data extraction, parsing, and per-locale JSON generation now lives in
the FFX data pipeline — see
[`docs/data-pipeline.md`](data-pipeline.md) and
[`REPO_BOUNDARY.md`](../REPO_BOUNDARY.md) for the split.

The `mappings/runtime/ffx-mappings.{locale}.json` bundles are the
runtime-loaded format consumed by `ParryModule.DataMapping.cs`. The
canonical producer is `build.cmd build-mod-runtime-bundles` in the
sibling pipeline repo. It reads exclusively from pipeline-owned canonical
outputs (`output/ffx/game_data/*_localized/`) and frozen script-text
snapshots (`inputs/script_text/`). Run it after any pipeline data refresh
to regenerate all 8 locale bundles.
