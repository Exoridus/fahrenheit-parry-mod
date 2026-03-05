# Data Pipeline Guide

This project uses external game data exports to build localized mapping bundles consumed at runtime.

## Workflow Order

```bash
.\build.cmd data-setup
.\build.cmd data-extract --vbfgamedatadir "C:\\Games\\Final Fantasy X-X2 - HD Remaster\\data"
.\build.cmd data-parse-all --dataroot ".workspace/data"
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
.\build.cmd data-extract --vbfgamedatadir "<GAME_DIR>\\data" --extractout ".workspace/data"
```

Single parser mode:

```bash
.\build.cmd data-parse --dataroot ".workspace/data" --datamode READ_ALL_COMMANDS
.\build.cmd data-parse --dataroot ".workspace/data" --datamode READ_MONSTER_LOCALIZATIONS --dataargs "de"
```

Batch parser modes:

```bash
.\build.cmd data-parse-all --dataroot ".workspace/data"
```

Import canonical mappings:

```bash
.\build.cmd map-import --locales us,de,fr,it,sp,jp,ch,kr --mapsource mappings/source
```

Build runtime bundles:

```bash
.\build.cmd map-build --locales us,de,fr,it,sp,jp,ch,kr --mapsource mappings/source --mapout mappings/runtime --mappublish mappings/runtime
```

Inventory and offload:

```bash
.\build.cmd data-inventory --datarootdir ".workspace/data"
.\build.cmd data-offload --nasdir "\\\\10.0.10.50\\data\\archive\\final-fantasy-assets" --offloadmode move --keepdatajunction true
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