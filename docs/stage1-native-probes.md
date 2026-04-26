# Stage-1 Native Observe Probes

Log-only hooks for the seven Stage-1 entry points listed in the FFX
knowledge-base probe plan
(`docs/fahrenheit-parry-probe-plan.md` in `ffx-knowledge-base`). They
exist to gather call-order and frame-timing evidence for the action /
camera / motion paths next to the existing damage hooks. **They never
mutate game state, never inject input, and never change parry
behaviour.**

## What gets hooked

| Probe | Goal |
| --- | --- |
| `MsActionRequest` | Action-dispatch entry point — order vs. damage hooks. |
| `MsCalcCommand` | Command calc — order vs. damage / CTB. |
| `MsCheckStatusBeforeAction` | Pre-action predicate / gate behaviour. |
| `MsLimitTypeDamageCheck` | Damage / overdrive bridge. |
| `MsAtelRequestMagicCamera` | Magic-camera request timing. |
| `op_et_battle_genko_counter_get` | Native counterattack path (observe only). |
| `MsSetMotion` | Motion setter — infer signature / motion IDs. |

Every hook calls orig exactly once, preserves its return value, and
swallows any formatting exception into a single `probe_fault` log line.

## Enabling probe logging

Probe **installation itself** is gated on `nativeProbeLogging`. With
the option off (the default), the seven Stage-1 hooks are not even
constructed — default play is identical to a build without this
file. To enable:

1. Edit your settings file (path printed at mod load by Fahrenheit;
   typically `Documents\My Games\Fahrenheit\modSettings\<mod-id>.json`
   or under the configured Fahrenheit data dir):

   ```json
   {
     "nativeProbeLogging": true
   }
   ```

2. Restart the game. Probe install happens once, in `init()`, after
   settings load. There is no in-game UI toggle for this option — it
   is a diagnostics flag, not a player-facing feature.

3. The mod logs `Stage-1 native probes installed (NativeProbeLogging=true)`
   once at startup when the install path runs.

## Where logs go

Probe events are pushed into a fixed-capacity in-memory ring
(`NativeProbeRing`, 4096 entries) and drained once per
`on_pre_update` tick to the **session debug log** that the rest of
the mod writes to (same file as `[MsSetDamage]`, `[MsCalcDamage]`,
etc.). No new log file is introduced.

A typical line:

```
[stage1.MsActionRequest] f=12345 target_id=2 attacker_id=10 p3=0x0 p4=0x0 p5=0x0 p6=0x0 ret=0x1 state=Open atk=10 pwa=1
```

Fields:

- `f=` — frame index (`_debugFrameIndex`)
- key=value tokens — known call args / return value, hex for opaque
  flags, decimal for ids
- `state=` — current `ParryInputState` (`Ready` / `Open` / `Resolved`
  / `WhiffLockout`)
- `atk=` — current battle attacker slot (0 if none)
- `pwa=` — `1` if the parry window is currently open, else `0`

Probe-fault lines look like:

```
[stage1.MsSetMotion] f=99 probe_fault reason="<exception message>"
```

orig was called regardless; only the formatting branch faulted.

## Throttling

Each probe has its own `PerFrameProbeThrottle` capped at **8 events
per frame** (constant `Stage1ProbeMaxPerFrame` in
`ParryModule.Stage1Probes.cs`). Worst-case across all seven probes is
56 events per frame against a 4096-entry ring — comfortably under the
ring's ~3-frame budget. If a probe ever blows past this, the dropped
count surfaces via the existing `[probe] dropped N event(s) due to
ring overflow` line that `drain_probe_ring` emits.

## Suggested traces to collect

To make later analysis worthwhile, each capture should be a single
clean session (no menu reloads) that exercises one path:

- **action vs. damage ordering** — Tidus normal attack on a single
  enemy; capture from "encounter started" to "turn end".
- **magic camera timing** — Yuna Esuna on Tidus; same boundaries.
- **counterattack path** — fight any enemy that auto-counters
  (Funguar, Ipiria); attack once, observe `op_et_battle_genko_counter_get`.
- **motion shape** — short fight with mixed physical and magic to
  collect `MsSetMotion` arg values across a few different cues.
- **status gate** — confused / berserked party member trying to act,
  to see whether `MsCheckStatusBeforeAction` returns differently than
  a healthy actor.

Always capture with `nativeProbeLogging=true` and the existing
`logging=true`, so probe lines and damage lines land in the same
session log.

## What NOT to infer yet

Stage-1 is observation only. **Do not** treat the probe output as
authoritative for any of the following until a follow-up change
adds explicit dereference / verification:

- Ordering between probes vs. `MsCalcDamage` / `MsSetDamage` is
  evidence for hypothesis-building, not contract-level correctness —
  they fire from different call sites and may interleave.
- `MsActionRequest`'s six args are typed as `int` per Ghidra's
  inference. `target_id` and `attacker_id` look like slot ids in
  early traces, but the remaining four (`p3`..`p6`) MUST NOT be
  treated as flags / pointers without separate verification.
- `MsLimitTypeDamageCheck` pointer args are logged as raw `nint`.
  **Do not dereference** — the pointer types in Ghidra
  (`Chr *attacker`, `Chr *target`) are inferred, not confirmed
  against this mod's `Chr` layout.
- `MsCalcCommand`, `MsCheckStatusBeforeAction`,
  `MsAtelRequestMagicCamera`, and `op_et_battle_genko_counter_get`
  are declared as 0-arg cdecl per Ghidra's "unknown" calling
  convention. If captures show them firing reliably without crashes
  for a session, that is weak evidence the 0-arg shape is close
  enough; it is **not** a full signature confirmation.
- `MsSetMotion` arg names (`p1`, `p2`, `chr_id`, `p4`...) are placeholders.
  Only `chr_id` has a Ghidra-confirmed semantic; everything else is
  raw integer / byte until separately verified.

Stage-2 work (action injection, animation control, camera
suppression, hooking the dispatch hub `MsBtlChrNumCheck`) is
explicitly out of scope for this PR. Do not enable it from this
file.
