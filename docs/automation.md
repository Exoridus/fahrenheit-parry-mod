# Automation Overview

`build.cmd` is the single local entrypoint.

_Auto-generated from build help metadata. Do not edit manually; run `.\build.cmd docs-sync`._

Quick command discovery:
- `.\build.cmd help`
- `.\build.cmd -h <workflow>`
- Bool parameters support both `--flag` and `--no-flag`.

## Core Workflows

- `.\build.cmd install`
  - Install/check local prerequisites.
  - Parameters:
  - --full (optional, default false) -> also install native build deps (MSBuild + vcpkg).
  - --dry-run (optional, default false) -> only print intended actions.
  - Examples:
  - `.\build.cmd install`
  - `.\build.cmd install --full`

- `.\build.cmd setup`
  - Prepare repository for local development.
  - Parameters:
  - No required parameters.
  - Examples:
  - `.\build.cmd setup`

- `.\build.cmd clean`
  - Remove generated local build outputs and preflight artifacts.
  - Parameters:
  - --full (optional, default false) -> also remove .release packaged outputs.
  - Examples:
  - `.\build.cmd clean`
  - `.\build.cmd clean --full`

- `.\build.cmd auto-deploy`
  - Configure automatic post-build deployment.
  - Parameters:
  - --game-dir <path> (optional) -> game install directory (must contain FFX.exe).
  - --refresh-game-dir (optional, default false) -> ignore saved GameDir and force detection/prompt flow.
  - Examples:
  - `.\build.cmd auto-deploy`
  - `.\build.cmd auto-deploy --game-dir "C:\Games\Final Fantasy X-X2 - HD Remaster"`

- `.\build.cmd doctor`
  - Diagnose local toolchain and environment state.
  - Parameters:
  - --full (optional, default false) -> include native/full-build tool checks.
  - Examples:
  - `.\build.cmd doctor`
  - `.\build.cmd doctor --full`

- `.\build.cmd format`
  - Apply code formatting/style fixes using dotnet format.
  - Parameters:
  - No required parameters.
  - Examples:
  - `.\build.cmd format`

- `.\build.cmd docs-sync`
  - Regenerate docs/automation.md from build help metadata.
  - Parameters:
  - No required parameters.
  - Examples:
  - `.\build.cmd docs-sync`

- `.\build.cmd lint`
  - Run fast lint/compile checks for build, mod, and tests projects.
  - Parameters:
  - --config Debug|Release (optional, default Debug).
  - Examples:
  - `.\build.cmd lint`
  - `.\build.cmd lint --config Release`

- `.\build.cmd smoke`
  - Run quick sanity checks (build + required artifact assertions).
  - Parameters:
  - --config Debug|Release (optional, default Debug).
  - --payload mod|full (optional, default mod).
  - Examples:
  - `.\build.cmd smoke`
  - `.\build.cmd smoke --config Release --payload mod`

- `.\build.cmd verify`
  - Run local validation (build + tests + commit parser selftest) without deployment side effects.
  - Parameters:
  - --config Debug|Release (optional, default Debug).
  - --repo owner/repo (optional, used in generated links).
  - Examples:
  - `.\build.cmd verify`
  - `.\build.cmd verify --config Release --repo Exoridus/fahrenheit-parry-mod`

- `.\build.cmd build`
  - Build mod-only or full Fahrenheit payload.
  - Parameters:
  - --payload mod|full (optional, default mod).
  - --config Debug|Release (optional, default Debug local / Release CI).
  - --deploy or --no-deploy (optional) -> override AutoDeploy from settings for this run.
  - --dry-run (optional, default false) -> simulate deploy sync actions without writing files.
  - Examples:
  - `.\build.cmd build`
  - `.\build.cmd build --payload full --config Release`

- `.\build.cmd deploy`
  - Deploy build artifacts into GameDir.
  - Parameters:
  - --payload mod|full (optional, default mod).
  - --game-dir <path> (optional if configured in dev.local.json).
  - --refresh-game-dir (optional, default false) -> ignore saved GameDir and force re-detection.
  - --config Debug|Release (optional, default Debug).
  - --dry-run (optional, default false) -> simulate deploy sync actions without writing files.
  - Examples:
  - `.\build.cmd deploy`
  - `.\build.cmd deploy --payload full --game-dir "C:\Games\Final Fantasy X-X2 - HD Remaster"`

- `.\build.cmd start`
  - Launch the game via deployed Fahrenheit stage0 loader.
  - Parameters:
  - --game-dir <path> (optional if configured).
  - --refresh-game-dir (optional, default false) -> ignore saved GameDir and force re-detection.
  - --elevated or --no-elevated (optional, default false).
  - Examples:
  - `.\build.cmd start --game-dir "C:\Games\Final Fantasy X-X2 - HD Remaster"`
  - `.\build.cmd start --game-dir "C:\Games\Final Fantasy X-X2 - HD Remaster" --elevated`

## Data + Mappings

- `.\build.cmd discord-sync`
  - Export Discord channels/threads into .workspace/discord with auto full-or-delta behavior and a per-server Media folder by default.
  - Parameters:
  - --guild <serverId> (required).
  - --channels <id1,id2,...> (optional) -> restrict export to explicit channel/thread IDs.
  - --full (optional, default false) -> force full refresh for every discovered channel/thread.
  - --discord-config <path> (optional, default .workspace/discord/config.local.json).
  - --discord-out-dir <path> (optional, default .workspace/discord).
  - --discord-include-threads none|active|all (optional, default config/all).
  - --discord-include-vc or --no-discord-include-vc (optional, default config/true).
  - --discord-media or --no-discord-media (optional, default config/true).
  - config blacklist[] (optional, local-only) -> filter known inaccessible/unsupported channel IDs before sync.
  - --discord-media-dir <path> (optional, advanced) -> override the default server-local Media directory.
  - Examples:
  - `.\build.cmd discord-sync --guild 612363389003366405`
  - `.\build.cmd discord-sync --guild 1328407223528853598 --channels 1328424139832168572`
  - `.\build.cmd discord-sync --guild 612363389003366405 --full`

- `.\build.cmd data-setup`
  - Install/update data tooling (VBFTool + FFXDataParser).
  - Parameters:
  - --parser-repo <url> (optional).
  - --parser-dir <path> (optional).
  - --parser-ref <git-ref> (optional).
  - --vbf-api <url> (optional).
  - --vbf-dir <path> (optional).
  - Examples:
  - `.\build.cmd data-setup`
  - `.\build.cmd data-setup --parser-ref main`

- `.\build.cmd ghidra-setup`
  - Install/update Ghidra into a repo-local tools directory.
  - Parameters:
  - --ghidra-api <url> (optional, default latest NSA release API).
  - --ghidra-dir <path> (optional, default .workspace/tools/ghidra).
  - Examples:
  - `.\build.cmd ghidra-setup`
  - `.\build.cmd ghidra-setup --ghidra-dir .workspace/tools/ghidra`

- `.\build.cmd ghidra-start`
  - Start the repo-local Ghidra launcher.
  - Parameters:
  - --ghidra-dir <path> (optional, default .workspace/tools/ghidra).
  - Examples:
  - `.\build.cmd ghidra-start`
  - `.\build.cmd ghidra-start --ghidra-dir .workspace/tools/ghidra`

- `.\build.cmd data-extract`
  - Extract FFX/FFX-2 data archives with VBFTool.
  - Parameters:
  - --vbf-game-dir <path> (optional, defaults to detected GameDir\\data).
  - --extract-out <path> (optional, default .workspace/data).
  - --extract-meta-menu or --no-extract-meta-menu (optional, default true).
  - Examples:
  - `.\build.cmd data-extract --vbf-game-dir "C:\Games\Final Fantasy X-X2 - HD Remaster\data"`
  - `.\build.cmd data-extract --extract-out .workspace/data`

- `.\build.cmd data-parse`
  - Run one parser mode and capture output as txt.
  - Parameters:
  - --data-mode <MODE> (optional, default READ_ALL_COMMANDS).
  - --data-args "<arg1> <arg2>" (optional).
  - --data-root <path> (optional, must contain ffx_ps2).
  - --data-out <path> (optional, default .workspace/data/ffx-dataparser).
  - Examples:
  - `.\build.cmd data-parse --data-mode READ_MONSTER_LOCALIZATIONS --data-args "de"`
  - `.\build.cmd data-parse --data-mode PARSE_ALL_BATTLES`

- `.\build.cmd data-parse-all`
  - Run the configured parser mode batch and capture all outputs.
  - Parameters:
  - --data-batch "MODE1;MODE2 arg" (optional, default built-in batch).
  - --data-root <path> (optional, must contain ffx_ps2).
  - --data-out <path> (optional, default .workspace/data/ffx-dataparser).
  - Examples:
  - `.\build.cmd data-parse-all --data-root .workspace/data`
  - `.\build.cmd data-parse-all --data-batch "READ_ALL_COMMANDS;READ_MONSTER_LOCALIZATIONS de"`

- `.\build.cmd map-import`
  - Generate canonical locale/domain mapping JSON from parser outputs.
  - Parameters:
  - --map-source <path> (optional, default mappings/source).
  - --locales us,de,... (optional, default us,de).
  - --data-out <path> (optional parser output root).
  - Examples:
  - `.\build.cmd map-import --locales us,de,fr,it,sp,jp,ch,kr`
  - `.\build.cmd map-import --map-source mappings/source`

- `.\build.cmd map-build`
  - Build runtime mapping bundles from canonical mapping JSON.
  - Parameters:
  - --map-source <path> (optional, default mappings/source).
  - --map-out <path> (optional, default mappings/runtime).
  - --map-publish <path> (optional, default mappings/runtime).
  - --locales us,de,... (optional, default us,de).
  - Examples:
  - `.\build.cmd map-build --locales us,de,fr,it,sp,jp,ch,kr`
  - `.\build.cmd map-build --map-out mappings/runtime --map-publish mappings/runtime`

- `.\build.cmd data-inventory`
  - Generate DATA_TREE.txt summaries for extracted data folders.
  - Parameters:
  - --data-root-dir <path> (optional, default .workspace/data).
  - --folders "name1;name2" (optional, default auto-detect under data root).
  - Examples:
  - `.\build.cmd data-inventory`
  - `.\build.cmd data-inventory --data-root-dir .workspace/data --folders "ffx_data;ffx-2_data"`

- `.\build.cmd data-offload`
  - Move or copy large extracted data folders to NAS and optionally keep junctions.
  - Parameters:
  - --nas-dir <unc-path> (required).
  - --offload-mode move|copy (optional, default move).
  - --keep-data-junction or --no-keep-data-junction (optional, default false).
  - --data-root-dir <path> (optional, default .workspace/data).
  - --folders "name1;name2" (optional).
  - Examples:
  - `.\build.cmd data-offload --nas-dir "\\10.0.10.50\data\archive\final-fantasy-assets"`
  - `.\build.cmd data-offload --nas-dir "\\10.0.10.50\data\archive\final-fantasy-assets" --offload-mode move --keep-data-junction`

## Release Workflows

- `.\build.cmd release-bump`
  - Bump version, regenerate changelog, pin Fahrenheit ref, create release commit + tag.
  - Parameters:
  - --bump patch|minor|major (optional, default patch).
  - --repo owner/repo (optional, improves links in notes/changelog).
  - Examples:
  - `.\build.cmd release-bump`
  - `.\build.cmd release-bump --bump minor --repo Exoridus/fahrenheit-parry-mod`

- `.\build.cmd release-ready`
  - Run release preflight (clean tree, commit checks, verify, release build, package dry-run, notes).
  - Parameters:
  - --range <BASE..HEAD> (optional, auto-derived if omitted).
  - --repo owner/repo (optional).
  - --tag vX.Y.Z (optional, used for dry-run notes/packages).
  - Examples:
  - `.\build.cmd release-ready --repo Exoridus/fahrenheit-parry-mod`
  - `.\build.cmd release-ready --range v0.0.1..HEAD --tag v0.0.2`

- `.\build.cmd release-pack`
  - Package built release payloads into ZIP archives + SHA256 files.
  - Parameters:
  - --tag vX.Y.Z (required).
  - --deploy-dir <path> (optional, default .workspace/fahrenheit/artifacts/deploy/rel).
  - --out-dir <path> (optional, default .release).
  - Examples:
  - `.\build.cmd release-pack --tag v0.0.1`
  - `.\build.cmd release-pack --tag v0.0.1 --out-dir .release`

- `.\build.cmd release-notes`
  - Generate release-notes markdown/text for a tag.
  - Parameters:
  - --tag vX.Y.Z (required).
  - --repo owner/repo (required).
  - --out <path> (optional, default .release/release-notes.txt).
  - Examples:
  - `.\build.cmd release-notes --tag v0.0.1 --repo Exoridus/fahrenheit-parry-mod`
  - `.\build.cmd release-notes --tag v0.0.1 --repo Exoridus/fahrenheit-parry-mod --out .release/release-notes.txt`

## Commit Workflows

- `.\build.cmd commit`
  - Create a Conventional Commit (wizard or direct flags).
  - Parameters:
  - --type feat|fix|... (optional, default chore).
  - --scope <scope> (optional).
  - --subject "message" (required in non-interactive mode).
  - --breaking or --no-breaking (optional, default false).
  - Examples:
  - `.\build.cmd commit`
  - `.\build.cmd commit --type feat --scope ui --subject "add queue table"`

- `.\build.cmd commit-check`
  - Validate one commit message.
  - Parameters:
  - --commit-file <path> or --message "..." (one is required).
  - Examples:
  - `.\build.cmd commit-check --commit-file .git/COMMIT_EDITMSG`
  - `.\build.cmd commit-check --message "feat: add timeline panel"`

- `.\build.cmd commit-range`
  - Validate commit subjects in a git range.
  - Parameters:
  - --range <BASE..HEAD> (required).
  - Examples:
  - `.\build.cmd commit-range --range origin/main..HEAD`

