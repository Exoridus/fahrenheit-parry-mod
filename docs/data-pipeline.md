# Data Pipeline Guide

This project uses external game data exports to build localized mapping bundles consumed at runtime.

## Workflow Order

```bash
.\build.cmd data-setup
.\build.cmd data-extract --vbf-game-dir "C:\\Games\\Final Fantasy X-X2 - HD Remaster\\data"
.\build.cmd data-parse-all --data-root ".workspace/data"
.\build.cmd map-import --locales us,de,fr,it,sp,jp,ch,kr
.\build.cmd map-build --locales us,de,fr,it,sp,jp,ch,kr
```

## Commands

Tooling setup:

```bash
.\build.cmd data-setup
```

VBF extraction:

```bash
.\build.cmd data-extract --vbf-game-dir "<GameDir>\\data" --extract-out ".workspace/data"
```

Single parser mode:

```bash
.\build.cmd data-parse --data-root ".workspace/data" --data-mode READ_ALL_COMMANDS
.\build.cmd data-parse --data-root ".workspace/data" --data-mode READ_MONSTER_LOCALIZATIONS --data-args "de"
```

Batch parser modes:

```bash
.\build.cmd data-parse-all --data-root ".workspace/data"
```

Import canonical mappings:

```bash
.\build.cmd map-import --locales us,de,fr,it,sp,jp,ch,kr --map-source mappings/source
```

Build runtime bundles:

```bash
.\build.cmd map-build --locales us,de,fr,it,sp,jp,ch,kr --map-source mappings/source --map-out mappings/runtime --map-publish mappings/runtime
```

Inventory and offload:

```bash
.\build.cmd data-inventory --data-root-dir ".workspace/data"
.\build.cmd data-offload --nas-dir "\\\\10.0.10.50\\data\\archive\\final-fantasy-assets" --offload-mode move --keep-data-junction
```

## Mapping Layout

Canonical source:
- `mappings/source/{locale}/{domain}.json`

Runtime bundles:
- `mappings/runtime/ffx-mappings.{locale}.json`
- `mappings/runtime/ffx-mappings.json` (US alias)

The mod loads runtime bundles from `mappings/runtime` in deployed output.

## Notes

- Data extraction/parsing is optional for gameplay; it is only needed when refreshing mapping datasets.
- Runtime builds and releases consume generated JSON bundles and do not require re-parsing by default.
