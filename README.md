<div align="center">

<!----><a name="top"></a>
# Fahrenheit Parry Mod
[![Latest](https://img.shields.io/github/v/release/Exoridus/fahrenheit-parry-mod?sort=semver&display_name=release&style=for-the-badge&logo=github&labelColor=1a1e23&color=#1a1e23)](https://github.com/Exoridus/fahrenheit-parry-mod/releases/latest)
[![Builds](https://img.shields.io/github/actions/workflow/status/Exoridus/fahrenheit-parry-mod/ci.yml?style=for-the-badge&label=Builds&logo=githubactions&logoColor=ffffff&labelColor=1a1e23)](https://github.com/Exoridus/fahrenheit-parry-mod/actions/workflows/ci.yml)
[![Sponsor](https://img.shields.io/badge/Sponsor-1a1e23?style=for-the-badge&logo=githubsponsors)](https://github.com/sponsors/Exoridus)
![Banner](https://github.com/Exoridus/fahrenheit-parry-mod/blob/main/resources/Banner.png?raw=true)

</div>

## Description

`fahrenheit-parry-mod` is a standalone mod project for the Fahrenheit framework that adds timing-based parry gameplay to **Final Fantasy X / X-2 HD Remaster**.

## End-User Install

Download the latest release assets:

- `fahrenheit-full-<tag>.zip` (recommended)
- `fhparry-mod-<tag>.zip` (for existing Fahrenheit installs)
- `*.sha256` checksum files

### Full Package

1. Open your game install directory (contains `FFX.exe`).
2. Extract `fahrenheit-full-<tag>.zip` there.
3. Ensure `.NET Runtime 10` is installed (or allow `start-fahrenheit.cmd` to install it via winget when prompted).
4. Start with `fahrenheit/start-fahrenheit.cmd` (recommended).

### Mod-Only Package

1. Ensure Fahrenheit is already installed.
2. Extract `fhparry-mod-<tag>.zip` into `GAME_DIR/fahrenheit/mods/`.
3. Ensure `GAME_DIR/fahrenheit/mods/loadorder` contains `fhparry`.

## Contributor Start

```bash
.\build.cmd setup
.\build.cmd doctor
.\build.cmd verify
```

## Common Commands

```bash
.\build.cmd help
.\build.cmd -h build
.\build.cmd format

.\build.cmd build --target Debug
.\build.cmd deploy --game-dir "C:\\Games\\Final Fantasy X-X2 - HD Remaster"
.\build.cmd start --game-dir "C:\\Games\\Final Fantasy X-X2 - HD Remaster"

.\tools.cmd help
.\tools.cmd ghidra-setup
.\tools.cmd ghidra-start

.\build.cmd release-ready --repo Exoridus/fahrenheit-parry-mod
.\build.cmd release-bump --bump patch --repo Exoridus/fahrenheit-parry-mod
```

---

**[Back to Top](#top)**
