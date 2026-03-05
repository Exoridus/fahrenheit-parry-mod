<div align="center">

<!----><a name="top"></a>
# Fahrenheit Parry Mod
[![Latest](https://img.shields.io/github/v/release/Exoridus/fahrenheit-parry-mod?style=for-the-badge&label=Latest&logo=github&color=44cc11)](https://github.com/Exoridus/fahrenheit-parry-mod/releases/latest)
[![Builds: Passing](https://img.shields.io/github/actions/workflow/status/Exoridus/fahrenheit-parry-mod/ci.yml?style=for-the-badge&label=Builds%3A%20Passing&logo=githubactions&color=44cc11)](https://github.com/Exoridus/fahrenheit-parry-mod/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/actions/workflow/status/Exoridus/fahrenheit-parry-mod/release.yml?style=for-the-badge&label=Release&logo=githubactions&color=44cc11)](https://github.com/Exoridus/fahrenheit-parry-mod/actions/workflows/release.yml)
![Banner](https://github.com/Exoridus/fahrenheit-parry-mod/blob/main/resources/Banner.png?raw=true)

</div>

## Description

`fahrenheit-parry-mod` is a standalone mod project for the Fahrenheit framework that adds timing-based parry gameplay to **Final Fantasy X / X-2 HD Remaster**.

## Features

### Implemented

- [x] Standalone repo workflow (no in-tree Fahrenheit source required)
- [x] Single-entrypoint automation via `build.cmd`
- [x] Deterministic build/deploy/release pipeline
- [x] Difficulty-based parry timing model with anti-spam tiers
- [x] Runtime localized mapping bundles (`mappings/runtime`)
- [x] CI verify matrix (`Debug` + `Release`)
- [x] Release packaging + release-note generation
- [x] Quality workflows: `doctor`, `lint`, `smoke`, `verify`

### Planned

- [ ] Gameplay tuning based on broader encounter coverage
- [ ] Optional additional overlay/table filtering controls
- [ ] Optional release signing pipeline

## End-User Install

Download the latest release assets:

- `fahrenheit-full-<tag>.zip` (recommended)
- `fhparry-mod-<tag>.zip` (for existing Fahrenheit installs)
- `*.sha256` checksum files

### Full Package

1. Open your game install directory (contains `FFX.exe`).
2. Extract `fahrenheit-full-<tag>.zip` there.
3. Launch through your Fahrenheit loader flow.

### Mod-Only Package

1. Ensure Fahrenheit is already installed.
2. Extract `fhparry-mod-<tag>.zip` into `GAME_DIR/fahrenheit/mods/`.
3. Ensure `GAME_DIR/fahrenheit/mods/loadorder` contains `fhparry`.

## Contributor Start

```bash
.\build.cmd install --full
.\build.cmd setup
.\build.cmd doctor
.\build.cmd verify
```

Detailed docs:

- Development and workflows: [docs/development.md](docs/development.md)
- Data extraction and mappings pipeline: [docs/data-pipeline.md](docs/data-pipeline.md)
- Target/command reference: [docs/automation.md](docs/automation.md)
- Community guide roundup (Discord export): [docs/community-guides.md](docs/community-guides.md)
- Local config schema: [docs/dev-local.schema.json](docs/dev-local.schema.json)

## Common Commands

```bash
.\build.cmd help
.\build.cmd -h build

.\build.cmd build --payload mod --config Debug
.\build.cmd deploy --gamedir "C:\\Games\\Final Fantasy X-X2 - HD Remaster"
.\build.cmd start --gamedir "C:\\Games\\Final Fantasy X-X2 - HD Remaster"
.\build.cmd ghidra-setup
.\build.cmd ghidra-start

.\build.cmd release-ready --repo Exoridus/fahrenheit-parry-mod
.\build.cmd release-bump --bump patch --repo Exoridus/fahrenheit-parry-mod
```

## Assets

- `resources/Banner.png`
- `resources/Logo.png`

---

**[Back to Top](#top)**
