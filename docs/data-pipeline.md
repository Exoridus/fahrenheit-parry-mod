# Data Pipeline Guide

This project uses external game data exports to build localized mapping bundles consumed at runtime.

## Workflow Order

```bash
.\tools.cmd data-setup
.\tools.cmd data-extract --vbf-game-dir "C:\\Games\\Final Fantasy X-X2 - HD Remaster\\data"
.\tools.cmd data-parse-all --input-dir ".workspace/data"
.\tools.cmd map-import --locales us,de,fr,it,sp,jp,ch,kr
.\tools.cmd map-build --locales us,de,fr,it,sp,jp,ch,kr
```

## Commands

Tooling setup:

```bash
.\tools.cmd data-setup
```

VBF extraction:

```bash
.\tools.cmd data-extract --vbf-game-dir "<GameDir>\\data" --extract-out ".workspace/data"
```

Single parser mode:

```bash
.\tools.cmd data-parse --input-dir ".workspace/data" --data-mode READ_ALL_COMMANDS
.\tools.cmd data-parse --input-dir ".workspace/data" --data-mode READ_MONSTER_LOCALIZATIONS --data-args "de"
```

Batch parser modes:

```bash
.\tools.cmd data-parse-all --input-dir ".workspace/data"
```

Import canonical mappings:

```bash
.\tools.cmd map-import --locales us,de,fr,it,sp,jp,ch,kr --map-source mappings/source
```

Build runtime bundles:

```bash
.\tools.cmd map-build --locales us,de,fr,it,sp,jp,ch,kr --map-source mappings/source --map-out mappings/runtime --map-publish mappings/runtime
```

Inventory and offload:

```bash
.\tools.cmd data-inventory --data-root-dir ".workspace/data"
.\tools.cmd data-offload --nas-dir "\\\\10.0.10.50\\data\\archive\\final-fantasy-assets" --offload-mode move --keep-data-junction
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
