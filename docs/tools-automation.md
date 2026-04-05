# Tools Automation Overview

`tools.cmd` contains local-only tooling workflows.

_Auto-generated from tools workflow metadata. Do not edit manually; run `.\build.cmd docs-sync`._

Quick command discovery:
- `.\tools.cmd help`
- `.\tools.cmd -h <workflow>`
- Bool parameters support both `--flag` and `--no-flag`.
- Global verbosity: `--verbosity|-v quiet|minimal|normal|detailed|diagnostic` (default: `normal`).
- Recommended escalation: `quiet` -> `normal` -> `detailed` -> `diagnostic`.
- Global config path: `--config-path` (shorthand: `-c`).
- Common shorthand: `-c <config-path>`, `-n` (`--dry-run`).
- Agent guidance: use `--verbosity quiet` for routine tooling workflows.

## Tooling Workflows (local-only)

- `.\tools.cmd discord-setup`
  - Install/update DiscordChatExporter CLI into .workspace/tools/DiscordChatExporter.
  - Parameters:
  - --discord-api <url> (optional).
  - Examples:
  - `.\tools.cmd discord-setup`

- `.\tools.cmd discord-sync`
  - Export Discord channels/threads into .workspace/discord.
  - Parameters:
  - --guild <serverId> (required).
  - --channels <id1,id2,...> (optional).
  - --full (optional).
  - Missing DiscordChatExporter is auto-ensured via tools.cmd discord-setup in interactive mode.
  - Workspace config uses strict PascalCase keys in .workspace/config.local.json (DiscordToken, VisionApiUrl, VisionApiKey, VisionModel, FetchRetries).
  - Discord workflow config uses strict PascalCase keys in .workspace/discord/config.local.json (Blacklist[], Guilds[]).
  - Examples:
  - `.\tools.cmd discord-sync --guild 612363389003366405`

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
  - --input-dir <path> (optional).
  - --out-dir <path> (optional).
  - Missing tooling is auto-ensured via tools.cmd data-setup in interactive mode.
  - Examples:
  - `.\tools.cmd data-parse --data-mode READ_ALL_COMMANDS`

- `.\tools.cmd data-parse-all`
  - Run configured parser mode batch and capture all outputs.
  - Parameters:
  - --data-batch "MODE1;MODE2 arg" (optional).
  - --input-dir <path> (optional).
  - --out-dir <path> (optional).
  - Missing tooling is auto-ensured via tools.cmd data-setup in interactive mode.
  - Examples:
  - `.\tools.cmd data-parse-all`

- `.\tools.cmd map-import`
  - Generate canonical locale/domain mapping JSON from parser outputs.
  - Parameters:
  - --map-source <path> (optional).
  - --locales us,de,... (optional).
  - --out-dir <path> (optional).
  - Requires existing parser outputs under --out-dir. Run: .\tools.cmd data-parse-all
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
  - Missing Ghidra is auto-ensured via tools.cmd ghidra-setup in interactive mode.
  - Examples:
  - `.\tools.cmd ghidra-start`

