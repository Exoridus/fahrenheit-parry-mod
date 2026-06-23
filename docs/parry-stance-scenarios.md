# Guard-Stance Scenarios — Design Reference

Visual-flow specification for the future "guard stance during parry window" feature (mod feature #2 in the parry-mod roadmap). Defines what should happen on screen for every combination of input timing, hit timing, status, and target mask the engine can produce, so the implementation has unambiguous targets to satisfy.

The stance itself reuses the engine's existing motion IDs `0x3C` / `0x3D` (the same poses fired by the in-game **Defend** command — see `combat-pipeline.md` § Defense Pipeline in the knowledge-base repo). They are guard-style poses that the engine already animates correctly, so we don't need any custom assets.

## Definitions

| Term | Meaning |
|---|---|
| **Stance ON** | Call `MsSetMotion(slot, 0x3C, …)` — character braces |
| **Stance OFF** | Call `MsSetMotion(slot, IDLE_MOTION_ID, …)` — character returns to idle |
| **Effect** | The shield/aura visual already implemented (`MsBtlSetHitEffect` 0x0C) |
| **SFX** | The existing audio playback (`play_feedback_sound`) |
| **Flinch** | The engine's normal hit-reaction animation (`MsDamageSetMotion` selects ID per damage type) |
| **Recovery lockout** | The whiff-recovery state, animation-approximated (300/450/600/750ms by difficulty) |

## Core scenarios (user-listed)

### 1. R1 way too early (no incoming hit yet, or window expires before hit lands)

- Press → Stance ON for *all defending slots* in the current parry mask
- Window timer expires (no hit) → Stance OFF, transition to whiff recovery lockout
- Lockout elapses → ready to listen again

**Visual**: `idle → guard → idle`. Brief pose that drops cleanly. No SFX, no effect.

### 2. R1 way too late (after the hit has already resolved)

Same as #1 from the player's perspective:

- Press is rejected silently if `_runtime.InputState != Ready` (we're past the window) — this is already handled by `ParryInputStateTransitions.DecidePress` returning `current_attack_already_parried` or `no_parryable_cue`
- **No stance change** — the press never reaches `transition_to_open`

If the press happens AFTER the hit but BEFORE the cue clears AND the state is somehow still Ready (race condition), it would behave like #1 but immediately whiff because the next damage event won't come.

**Visual**: nothing. Press is debounced.

### 3. R1 barely too early (window expires before the hit lands but the hit lands shortly after)

- Press → Stance ON
- Window timer expires → Stance OFF, transition to whiff recovery lockout
- Hit lands → engine's normal Flinch animation plays on top
- Recovery lockout proceeds independently — note: lockout is for INPUT gating, the visual flinch is the engine's own response to taking damage

**Visual**: `idle → guard → idle → flinch (engine-driven) → idle`. Stance and flinch don't visually conflict because they're sequential motion states, both calling the same `Ch_SetSysMotion` underneath. The engine handles the flinch interrupt naturally.

### 4. R1 on time (single hit, single target)

- Press → Stance ON
- Hit lands within window → Parry resolves: SFX + Effect + log
- Engine's flinch is **suppressed** (this is the existing damage-negation hook — `h_ms_set_damage_internal` returns early)
- Stance OFF after a brief hold (~200ms) so the player sees the brace pose
- State → Resolved (no whiff lockout — earned a clean parry)

**Visual**: `idle → guard → [shield flash + sfx] → guard hold (200ms) → idle`. The stance hold makes the parry feel weighty.

### 5. R1 on time, multiple attacks in a row (chain parry)

- Attack 1 incoming, press → Stance ON
- Attack 1 hits → Parry resolves: SFX + Effect, Stance still ON (waiting for cue clear)
- Cue clears, state → Ready, Stance starts dropping to idle
- **Attack 2 detected, press during the drop** → Stance back ON immediately (interrupt the drop)
- Attack 2 hits → Parry resolves: SFX + Effect
- And so on

**Visual**: `idle → guard → [parry] → guard (sustained through chain) → ... → idle (after chain ends)`. The stance "snaps" back without dropping fully — gives the chain a connected feel.

**Implementation detail**: re-pressing during the idle-return interpolation calls `MsSetMotion(slot, 0x3C, blend_frames=2, …)` with a short blend time to crisply re-engage. The engine's motion blender handles the partial-state transition.

### 6. R1 while in flinch animation (interrupt flinch into stance for next attack)

This is the inverse of #3:

- Hit lands without parry → Flinch animation playing
- Next attack incoming, press during flinch → Stance ON (interrupt flinch)
- Outcome of next hit follows the relevant scenario (4 / 1 / etc.)

**Visual**: `idle → flinch → guard (interrupt) → [parry/whiff/etc.]`. Same `MsSetMotion` call as scenario 5 — engine's motion override is symmetric.

**Caveat**: if the flinch is the *killing* hit (HP=0), the death animation will override anything. We must not call `MsSetMotion` on a dead actor — gated by reading `chr->ram.hp == 0` or `field_0xdcc != 0` (death-pending flag).

## Additional scenarios (added)

### 7. Multi-target attack — partial parry (mixed slot outcomes)

Enemy attack targets 3 party members. Player presses → all 3 brace.

| Outcome per slot | Slot animation |
|---|---|
| Slot parried (window was open, hit landed in window) | Parry SFX + Effect, then idle |
| Slot took the hit (window already expired or somehow gated off) | Flinch, then idle |
| Slot was untargeted (e.g. dead, removed mid-attack) | No animation — stayed idle |

**Visual**: All 3 brace simultaneously, then visually diverge per-slot at impact. The streak counter (per-slot) reflects this naturally.

### 8. Random-target multi-target attack with all hits going to one slot

Some monster attacks (e.g. random-target multi-hits) roll targets at attack time. If all 3 hits roll to the same slot:

- Press → Stance ON for all defending slots in the mask
- Hits 1, 2, 3 all land on same slot
- Slot 1 parries hit 1: SFX + Effect
- Slot 1 parries hit 2: SFX + Effect (replays — fine)
- Slot 1 parries hit 3: SFX + Effect
- Other slots stay in stance (no hits to react to)
- All return to idle when cue clears

**Visual**: 3 sequential shield flashes on the same character. No collision — each hit is processed independently.

### 9. Confused / Petrified / Berserk character

Existing logic gates these via `is_target_non_parryable`. If the slot is non-parryable, **don't fire Stance ON for that slot** — they should look exactly like a normal hit-taker. The engine handles their state-driven motion (sleep idle, petrified pose, etc.) directly.

**Visual**: parryable slots brace; non-parryable slots stay in their status-driven pose. Engine's natural motion handles them.

### 10. Dead / KO'd character

`chr->ram.hp == 0` or `field_0xdcc != 0` — skip Stance ON entirely for that slot. Death animation has full priority.

### 11. Unblockable attack (Doom, Zanmato, scripted death)

Player presses → Stance ON. Cue's command flags include the "non-parryable" bit (we already detect this via the negation hook gate). The hit lands and is NOT prevented; flinch / death animation plays through.

**Visual**: Stance plays but the parry gives no benefit. **Open question**: should we visually distinguish this for the player? Two options:
- Drop the stance immediately on detecting non-parryable command (player sees "wasted brace")
- Let the stance play out normally so the input feels consistent (player learns the result from outcome, not animation)

Recommend option B for consistency — input-state and visual-state should never conflict.

### 12. Whiff lockout active

Press is rejected via `in_guard_recovery`. **No stance change** — character is already in the recovery animation.

**Visual**: nothing. The recovery anim continues; press is ignored.

### 13. Out of battle / in menus / cutscene

`_battleAdapter.GetBattle() == null` — gate is already in place (we just added it for the HUD). Same gate applies to stance: no stance triggers outside of battle.

### 14. Magical / non-physical attack (e.g. Wakka casting from far)

The cue is parryable per existing logic if the command is physical-class. Magic attacks are typically non-parryable in the existing mod design (gated in `is_magic_like_attack`). **No stance change** for magic-class attacks.

If we want stance for magic later, the gate is one boolean flip — but the visual probably reads as confusing because the player doesn't physically intercept magic.

### 15. Multi-hit attack (Anfunkeln, Blitzra, etc.)

Attack has multiple hit events on the same target. The existing parry resolution pipeline handles each hit individually via `_parryExpiry` per-slot. For stance:

- Press → Stance ON
- Hit 1: SFX + Effect (stays in stance — cue not cleared)
- Hit 2: SFX + Effect (still in stance)
- ...
- Cue clears → Stance OFF

**Visual**: One brace, multiple shield flashes at impact. Reads as "blocked the whole barrage".

### 16. Status-induced damage tick during enemy turn (poison, regen, zombie)

Engine processes these in `MsCheckStatusBeforeAction` — they are NOT parryable. **No stance**.

If a poison tick happens to coincide with a parry window being open for an unrelated enemy attack, the tick should not trigger any parry-mod state changes. The existing damage gate (`isActiveParry` checks `_parryArmedAttackerId[param_2] == _runtime.CurrentAttackerId`) prevents misattribution.

**Visual**: status numbers float as usual, no stance interaction.

## Implementation contract

When implementing #2, the spec is:

1. Hook into `transition_to_open` (window OPEN). For every slot in `partyMask` that is alive, not petrified/dead/non-parryable: call `MsSetMotion(slot, 0x3C, blend_frames, 0, 1, 0, 0)`.
2. Hook into the Resolved/Whiff transitions and the cue-cleared boundary. Restore idle motion via `MsSetMotion(slot, IDLE_MOTION_ID, blend_frames, 0, 1, 0, 0)` for slots that were braced.
3. When re-pressing during the drop (scenarios 5/6), the same `MsSetMotion` call replays — engine handles the blend.
4. Always gate writes by reading `chr->ram.hp` and `field_0xdcc` first to avoid overriding death state.
5. Use a short blend duration (~2-4 frames) so the stance feels responsive, not floaty.

The implementation should be idempotent — calling Stance ON on an already-stanced slot is a no-op cost-wise (engine just reaffirms the motion). No need to track per-slot stance state in mod-side memory.

## Open questions before implementation

- **`IDLE_MOTION_ID`**: there isn't a single "idle" motion ID — each character has a per-class idle. Need to either read the chr's current motion before stance-ON and restore it, or use a "neutral release" motion that the engine treats as "go back to whatever you were doing". Hypothesis: motion ID `0x00` or passing the chr's `field_0x40e` (last motion) back. Spike needed.
- **Stance hold after parry**: should the stance hold for ~200ms after the SFX/effect fires (scenario 4) before dropping, or drop immediately? UX call — recommend a brief hold so the parry visually resolves before the drop.
- **Per-slot vs. per-mask stance call**: is it cheaper to call MsSetMotion 7 times for a 7-target attack, or batch somehow? Engine probably has no batch API; 7 calls is fine.
