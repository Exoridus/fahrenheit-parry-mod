# External Memory Offset Map

This project keeps a curated map of externally researched FFX memory offsets in:

- `src/data/ExternalMemoryOffsetMap.cs`
- Domain partials in `src/data/ExternalMemoryOffsetMap.*.cs`
- Descriptor registry in `src/data/MemoryRegistry.cs`

`ExternalMemoryOffsetMap` is the single source of truth for validated offsets used by runtime code.

## Sources

- `coderwilson/FFX_TAS_Python` (`memory/main.py`)
- `HannibalSnekter/FFXSpeedrunMod` (`Resources/MemoryLocations.cs`)
- curated reverse-engineering notes and local validation runs
- see `docs/pointers-hooks-guide.md` for the runtime model and examples

## Scope

All values are static offsets relative to the FFX module base in Windows builds.
They are suitable for diagnostics, correlation, and reverse engineering assistance.
They are not guaranteed stable across all game versions/builds and should be runtime-validated.

## Groups

- `BattleFlags`: battle-active and coarse battle state flags.
- `TurnQueue`: CTB queue and current actor references.
- `BattleUi`: battle menu and target selection state.
- `Formation`: active party slots in battle.
- `BattlerStruct`: pointer+stride model for per-battler HP/MP/status fields.
- `ActorArray`: world actor list pointer/stride and actor coordinate fields.
- `FrameAndRng`: frame counter and RNG table base.
- `DiscordCandidates`: unverified candidate offsets/functions kept separate from validated gameplay constants.

## Related Types

- `MemoryLocation`: immutable descriptor (`Name`, `BaseOffset`, optional pointer chain).
- `MemoryRegistry`: typed wrappers around offset constants for diagnostics and tooling.
- `MemoryCandidates`: staging list for candidate offsets that require validation.

## Usage Pattern

1. Resolve game base address.
2. Compute absolute address as `base + offset`.
3. For pointer-based areas (`BattlerStruct`, `ActorArray`):
   - read pointer at `base + *Pointer`
   - compute entry as `pointer + stride * index`
   - read field at `entry + fieldOffset`

## Notes

Validation/promotion workflow:

1. Add candidate to `MemoryCandidates` with `Unverified` state.
2. Validate via runtime checks/tests in battle and non-battle states.
3. Promote only verified entries into `ExternalMemoryOffsetMap`.
4. Keep rejected entries out of runtime code paths.
