# Tools Automation Overview

`tools.cmd` contains local-only tooling workflows.

_Auto-generated from tools workflow metadata. Do not edit manually; run `.\build.cmd docs-sync`._

Quick command discovery:
- `.\tools.cmd help`
- `.\tools.cmd -h <workflow>`
- Bool parameters support both `--flag` and `--no-flag`.

## Tooling Workflows (local-only)

- `.\tools.cmd discord-sync`
  - Export Discord channels/threads into .workspace/discord.
  - Parameters:
  - --guild <serverId> (required).
  - --channels <id1,id2,...> (optional).
  - --full (optional).
  - --discord-utc or --no-discord-utc (optional).
  - Workspace config uses strict PascalCase keys in .workspace/config.local.json (DiscordToken, OpenApiUrl, OpenApiKey, OpenApiModel, FetchRetryCount).
  - Discord workflow config uses strict PascalCase keys in .workspace/discord/config.local.json (Blacklist[], Guilds[]).
  - Examples:
  - `.\tools.cmd discord-sync --guild 612363389003366405`

- `.\tools.cmd workspace-prune`
  - Remove regenerable local workspace artifacts using safe/deep presets.
  - Parameters:
  - --preset safe|deep (optional, default safe).
  - --dry-run (optional).
  - Examples:
  - `.\tools.cmd workspace-prune --preset safe --dry-run`
  - `.\tools.cmd workspace-prune --preset deep`

- `.\tools.cmd data-setup`
  - Install/update data tooling (VBFTool + FFXDataParser).
  - Parameters:
  - --parser-repo <url> (optional).
  - --parser-dir <path> (optional).
  - --parser-ref <git-ref> (optional).
  - --vbf-api <url> (optional).
  - --vbf-dir <path> (optional).
  - Examples:
  - `.\tools.cmd data-setup`

- `.\tools.cmd data-extract`
  - Extract FFX/FFX-2 data archives with VBFTool.
  - Parameters:
  - --vbf-game-dir <path> (optional).
  - --extract-out <path> (optional).
  - --extract-meta-menu or --no-extract-meta-menu (optional).
  - Examples:
  - `.\tools.cmd data-extract`

- `.\tools.cmd data-parse`
  - Run one parser mode and capture output as txt.
  - Parameters:
  - --data-mode <MODE> (optional).
  - --data-args "<arg1> <arg2>" (optional).
  - --data-root <path> (optional).
  - --data-out <path> (optional).
  - Examples:
  - `.\tools.cmd data-parse --data-mode READ_ALL_COMMANDS`

- `.\tools.cmd data-parse-all`
  - Run configured parser mode batch and capture all outputs.
  - Parameters:
  - --data-batch "MODE1;MODE2 arg" (optional).
  - --data-root <path> (optional).
  - --data-out <path> (optional).
  - Examples:
  - `.\tools.cmd data-parse-all`

- `.\tools.cmd map-import`
  - Generate canonical locale/domain mapping JSON from parser outputs.
  - Parameters:
  - --map-source <path> (optional).
  - --locales us,de,... (optional).
  - --data-out <path> (optional).
  - Examples:
  - `.\tools.cmd map-import`

- `.\tools.cmd map-build`
  - Build runtime mapping bundles from canonical mapping JSON.
  - Parameters:
  - --map-source <path> (optional).
  - --map-out <path> (optional).
  - --map-publish <path> (optional).
  - --locales us,de,... (optional).
  - Examples:
  - `.\tools.cmd map-build`

- `.\tools.cmd data-inventory`
  - Generate DATA_TREE.txt summaries for extracted data folders.
  - Parameters:
  - --data-root-dir <path> (optional).
  - --folders "name1;name2" (optional).
  - Examples:
  - `.\tools.cmd data-inventory`

- `.\tools.cmd data-offload`
  - Move or copy large extracted data folders to NAS and optionally keep junctions.
  - Parameters:
  - --nas-dir <unc-path> (required).
  - --offload-mode move|copy (optional).
  - --keep-data-junction or --no-keep-data-junction (optional).
  - --data-root-dir <path> (optional).
  - --folders "name1;name2" (optional).
  - Examples:
  - `.\tools.cmd data-offload --nas-dir "\\10.0.10.50\data\archive\final-fantasy-assets"`

- `.\tools.cmd ghidra-setup`
  - Install/update Ghidra into a repo-local tools directory.
  - Parameters:
  - --ghidra-api <url> (optional).
  - --ghidra-dir <path> (optional).
  - Examples:
  - `.\tools.cmd ghidra-setup`

- `.\tools.cmd ghidra-start`
  - Start the repo-local Ghidra launcher.
  - Parameters:
  - --ghidra-dir <path> (optional).
  - Examples:
  - `.\tools.cmd ghidra-start`

