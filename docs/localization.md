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

- `.\build.cmd map-import --locales us,de,...`
  - Generates canonical source domains in `mappings/source/<locale>/`.
- `.\build.cmd map-build --locales us,de,...`
  - Builds compact runtime bundles in `mappings/runtime/`.

This separation keeps source mappings editable while preserving fast runtime loading.
