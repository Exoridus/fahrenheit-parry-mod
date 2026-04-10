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

