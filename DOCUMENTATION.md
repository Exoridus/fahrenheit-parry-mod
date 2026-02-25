# Parry Prototype Documentation

## Goal
Provide a minimal reference implementation for battle-time hooks. The mod sits entirely in managed code and demonstrates how to

1. Inspect the live CTB attack cues stored in `FhFfx.Globals.Battle.btl->attack_cues`.
2. Track when an enemy starts/ends an attack turn.
3. Monitor controller input and give feedback when the player reacts within a short window.

## Limitations
- The current build relies on the heuristic that attack cue indices `>= 10` belong to enemies. This matches the observed runtime layout but may require refinement for edge cases (e.g., multi-phase battles).
- Damage windows are clamped by monitoring the live `Chr.damage_*` registers. We still do not detour the native ApplyDamage routine, so attacks that skip those registers would need extra handling.
- The audio feedback uses the Windows system sound for now. Hooking the in-game SFX banks requires mapping functions such as `iSfxSetId`/`SndEventSetSoundEvent`, which is left as future work.

## Usage
1. Copy the published `fhparry` folder into `fahrenheit/mods/` and add `fhparry` to `mods/loadorder` (do not add `fhruntime`; Fahrenheit prepends it automatically).
2. Enter a battle in FFX.
3. When an enemy begins a physical attack animation, press `R1` (mapped to `P` on keyboard by default) during the swing. The mod logs the hit and plays a short feedback sound.
4. Watch the Stage0 console or Fahrenheit logs for entries like `Parry input detected against attacker #12.`

## Timing Modes
- **Legacy window**: Keeps the original fixed-duration timer. The slider now only applies when this mode is selected.
- **ApplyDamage clamp**: Observes the party damage registers each frame and auto-closes the window the moment damage is applied. The resolve window slider caps how long the indicator can linger if no hit arrives.

## Source Walkthrough
- `src/module.cs`
  - `monitor_attack_cues()` polls the `attack_cues_size` byte every frame. Rising edges on the cue count mean a new attack step was scheduled.
  - `is_enemy_attacker()` currently treats indices >= 10 as enemy slots. This can be swapped with a proper lookup once more RE data is available.
  - The parry window lasts `ParryWindowMaxFrames` frames and closes automatically when the cue list empties or the timer expires.
  - `on_parry_success()` logs the result and emits an audible cue via `SystemSounds.Asterisk`.

## Extending Further
- Replace the heuristic with a lookup that inspects the `Chr` structs referenced by the attack cue (e.g., checking `stat_group`).
- Drive custom UI feedback by rendering inside `render_imgui()`.
- Hook into the damage-calculation routine using `FhMethodHandle` if you want to negate or reflect the attack when a parry is detected.
- Replace the placeholder sound with a call into the in-game SFX controllers once those functions are mapped.

