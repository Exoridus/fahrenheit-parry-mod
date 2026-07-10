# Overdrive mode indices — what this mod claims, and what it leaves alone

FFX has room for exactly **twenty** overdrive modes. Eighteen are the game's own. Two are unused. This
document exists so the next mod author does not silently collide with us.

## The budget

| Index | Mode | Owner |
|---|---|---|
| `0x00`–`0x10` | Warrior, Comrade, Stoic, Healer, Tactician, Victim, Dancer, Avenger, Slayer, Hero, Rook, Victor, Coward, Ally, Sufferer, Daredevil, Loner | FFX |
| `0x11` | Riposte — charges on parry and perfect dodge | **fhparry** |
| `0x12` | *(free)* | **deliberately unclaimed** |
| `0x13` | Aeons — not learnable, always on for aeons | FFX |

There is no third free index. The engine's menu-build loop is hard-capped at `i < 0x14`
(`FUN_008c2370` case 1), and the learn gate rejects any index `>= 0x11`
(`FUN_007b10d0`), so `0x11` and `0x12` are the entire remaining budget.

## What fhparry does with `0x11`

- Sets bit 17 of `limit_modes_obtained` (`PlySave + 0x88`) once the character has parried 100 times.
- Owns `limit_mode_counters[0x11]` (`PlySave + 0x60 + 0x11*2`), which the engine never touches: its
  learn gate is `mode < 0x11`.
- This runs unconditionally — it is a standard feature of the mod, not an opt-in. It **writes into
  `save_ram`**: the per-character learn progress, and the eventual unlock (bit 17), are written into
  the live `PlySave` block, so if the player saves the game they become permanent in that save file.

## What fhparry does not do with `0x12`

Nothing. It is left free on purpose, so another Fahrenheit mod can add an overdrive mode without
fighting us for the last slot. A dodge-focused mode was considered and rejected: fhparry's `0x11`
already charges on perfect dodge, and splitting one defensive skill across two mutually exclusive
modes would weaken both.

## If you are that other mod author

Take `0x12`. Then note two things the engine will do to you.

**The display-order table decides visibility, not just the bit.** A mode appears in the selection menu
only when its index is a member of the 20-byte order table at `FFX.exe+0x88765C` **and** its bit is set
in that character's `limit_modes_obtained`. Runtime measurement of that table:

```
02 00 01 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10 11 12 13
```

Both `0x11` and `0x12` are already members. You do not need to hook the menu build.

**`MsLimitTypeProcess` will grant your mode behind your back.** Its loop runs `i < 0x14`, so it visits
your index every battle. If it finds `limit_mode_counters[yours] == 0` while your obtained-bit is
unset, it sets the bit itself and fires a "learned" message — triggered incidentally, whenever any
other character learns anything that round.

So: **never leave your counter at `0` while your bit is unset.** When granting, write the bit first and
the counter second. `0xFFFF` means "not applicable" and is the safe idle value — index `0x13` (Aeons)
sits at `0xFFFF` for every party member and demonstrates this working correctly in every battle.

## Sources

Offsets and semantics are derived from the decompilation and confirmed against a live save; the
per-character learning thresholds come from the Ultimania and were cross-checked against the same save
(`Rook` and `Hero` match on every character). See `.claude/agent-memory/ffx-forensics/` for the trail.
