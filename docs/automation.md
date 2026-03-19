# Automation Overview

`build.cmd` is the single local entrypoint.

Quick command discovery:
- `.\build.cmd help`
- `.\build.cmd -h <workflow>`

## Core Workflows

- `.\build.cmd install [--full] [--dryrun]`
  - Installs/checks prerequisites (`git`, `.NET SDK 10.x`).
  - `--full` also ensures native prerequisites (`MSBuild`, `vcpkg`).

- `.\build.cmd setup`
  - Configures local git hooks.
  - Runs Fahrenheit setup.
  - Runs interactive auto-deploy setup.

- `.\build.cmd clean [--full]`
  - Removes generated local outputs (`bin/`, `obj/`, Fahrenheit `artifacts`, preflight folders).
  - `--full` also removes `.release` packaged outputs.

- `.\build.cmd doctor [--full]`
  - Prints environment diagnostics.
  - Fails when required prerequisites are missing.

- `.\build.cmd auto-deploy [--gamedir <path>] [--mode none|update|mod-only]`
  - Configures automatic post-build deployment.

- `.\build.cmd lint [--config Debug|Release]`
  - Runs fast lint compile checks for build, mod, and tests projects.

- `.\build.cmd smoke [--payload mod|full] [--config Debug|Release]`
  - Runs quick build sanity checks and verifies required artifacts.

- `.\build.cmd verify [--config Debug|Release] [--repo owner/repo]`
  - Runs local verification (build + tests + commit parser selftest).

- `.\build.cmd build [--payload mod|full] [--config Debug|Release]`
  - `mod` = managed mod build.
  - `full` = full Fahrenheit build (native + managed).

- `.\build.cmd deploy [--payload mod|full] [--mode merge] [--config Debug|Release] [--gamedir <path>]`
  - Deploys artifacts to `GAME_DIR\fahrenheit\...`.

- `.\build.cmd start [--gamedir <path>] [--elevated]`
  - Launches `GAME_DIR\fahrenheit\bin\fhstage0.exe ..\..\FFX.exe`.
  - Fails early with guidance if .NET Runtime 10 host components are missing.

## Data + Mappings

- `.\build.cmd discord-sync --guild <serverId> [--full]`
  - Enumerates all accessible text channels and threads in the specified guild.
  - Voice channels are included by default because Discord voice channels can also contain chat messages.
  - `--channels <id1,id2,...>` restricts the run to specific channel/thread IDs.
  - Default behavior is auto mode:
  - if no prior channel export exists, do a full export
  - if a prior channel export exists, export only messages after the newest stored message ID and merge by message ID
  - Writes JSON exports into `.workspace/Discord`.
  - Reads the token and optional default CLI settings from local-only config (`.workspace/Discord/config.local.json`) or fallback `.workspace/dev.local.json`.
  - `config.local.json` can also contain a `blacklist` array of channel/thread IDs; blacklisted IDs are filtered before sync starts.
  - If a run still encounters an inaccessible or unsupported channel, its ID is appended back into that local blacklist automatically.
  - By default, downloaded assets go into `<Guild Root>\\Media`, so each server keeps its own shared media folder.
  - `--discordmediadir <path>` or config `defaults.mediaDir` is only needed if you want to override that server-local default.
  - Successful runs automatically remove `.workspace/Discord/_staging/<guildId>` unless `--discordcleanupstaging false` is used.
  - Use `--full` periodically because delta mode does not reconcile deleted messages, older edits, or older reaction changes.

- `.\build.cmd data-setup`
  - Installs/updates `VBFTool` + `FFXDataParser`.

- `.\build.cmd ghidra-setup [--ghidraapi <url>] [--ghidradir <path>]`
  - Installs/updates repo-local `Ghidra` in `.workspace/tools/ghidra`.

- `.\build.cmd ghidra-start [--ghidradir <path>]`
  - Starts `ghidraRun.bat` from the repo-local install.

- `.\build.cmd data-extract [--vbfgamedir <path>] [--extractout <path>] [--extractmetamenu true|false]`
  - Extracts `FFX_Data.vbf` and `FFX2_Data.vbf`.

- `.\build.cmd data-parse [--datamode <MODE>] [--dataargs "..."] [--dataroot <path>] [--dataout <path>]`
  - Runs one parser mode and writes captured `.txt` output.

- `.\build.cmd data-parse-all [--databatch "..."] [--dataroot <path>] [--dataout <path>]`
  - Runs a parser mode batch.

- `.\build.cmd map-import [--mapsource mappings/source] [--locales us,de,...] [--dataout <path>]`
  - Builds canonical mapping source files:
  - `mappings/source/{locale}/{domain}.json`

- `.\build.cmd map-build [--mapsource mappings/source] [--mapout mappings/runtime] [--mappublish mappings/runtime] [--locales us,de,...]`
  - Builds runtime mapping bundles:
  - `mappings/runtime/ffx-mappings.{locale}.json`
  - `mappings/runtime/ffx-mappings.json` (US alias)

- `.\build.cmd data-inventory [--datarootdir .workspace/data] [--folders "ffx_ps2;ffx-2_data"]`
  - Generates `DATA_TREE.txt` inventories.

- `.\build.cmd data-offload --nasdir "\\NAS\Share\ffx-data" [--offloadmode move|copy] [--keepdatajunction true]`
  - Offloads selected `.workspace/data` folders to NAS.

## Release Workflows

- `.\build.cmd release-bump [--bump patch|minor|major] [--repo owner/repo]`
  - Bumps manifest version.
  - Regenerates `CHANGELOG.md`.
  - Pins Fahrenheit release ref.
  - Creates release commit + annotated tag.

- `.\build.cmd release-ready [--range BASE..HEAD] [--repo owner/repo] [--tag vX.Y.Z]`
  - Preflight:
  - clean working tree check
  - commit range validation
  - verify
  - full release build
  - package dry-run
  - release notes dry-run

- `.\build.cmd release-pack --tag vX.Y.Z [--deploydir <path>] [--outdir <path>]`
  - Creates release ZIPs + SHA256 files.

- `.\build.cmd release-notes --tag vX.Y.Z --repo owner/repo [--out <path>]`
  - Generates release notes markdown.

## Commit Workflows

- `.\build.cmd commit [--type feat|fix|...] [--scope <scope>] [--subject "..."] [--breaking true|false]`
  - Interactive if `--subject` is omitted.

- `.\build.cmd commit-check --commitfile <path>` or `.\build.cmd commit-check --message "..."`
  - Validates one commit subject.

- `.\build.cmd commit-range --range BASE..HEAD`
  - Validates commit subjects in a range.


