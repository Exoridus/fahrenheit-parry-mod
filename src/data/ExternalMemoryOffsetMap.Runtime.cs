namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap
{
    public static class FrameAndRng
    {
        // Global frame counter.
        public const int FrameCounter = 0x0088FDD8;

        // RNG index table base (4-byte entries).
        public const int RngBase = 0x00D35ED8;
    }

    public static class StartupState
    {
        // Expected type: byte
        public const int MenuState = 0x00F407E4;
        
        // Expected type: uint
        public const int MoviePlay = 0x00D2A008;

        // Expected type: uint
        public const int StateD36FA0 = 0x00D36FA0;

        // Expected type: uint
        public const int StateD36FA4 = 0x00D36FA4;
    }

    public static class Functions
    {
        // Expected type: Action<byte, int, int> (MsDamageSetMotion)
        public const int MsDamageSetMotion = 0x0038CAE0;
        
        // Expected type: Func<Chr*, Chr*, Command*, int, int*, int, int> (DmgCalcArmored)
        public const int DmgCalcArmored = 0x0038AB80;
        
        // Expected type: Func<int, nint, int, nint, nint, int, nint, nint, nint, nint, int, int> (MsCalcDamageInternal)
        public const int MsCalcDamageInternal = 0x0038E680;

        // Expected type: Func<uint, char*> (AtelGetEventName)
        public const int AtelGetEventName = 0x004796e0;

        // MsAtelRequestCamera at FFX.exe+0x397BD0 — gate for in-game camera changes
        // (called from 12 sites including battle camera setup, scene transitions).
        // Used by the enemy-turn camera-lock feature to suppress camera moves while
        // the active actor is an enemy (so the player keeps the default view that
        // shows incoming attacks clearly).
        // Expected signature: int (int, int, int, int, int, int, int, int) — 8 params, return value unused at every observed call site.
        public const int MsAtelRequestCamera = 0x00397bd0;

        // MsBattleSpecialCameraPause at FFX.exe+0x39DDD0 (absolute 0x0079DDD0) —
        // engine's "enter cinematic camera mode" entry point. Boss / overdrive-class
        // enemy attacks (high-cue commands like 0x40XX) route through this path
        // instead of MsAtelRequestCamera, bypassing the existing camera-lock hooks.
        //
        // Signature: void (byte mode)
        //
        // Used by the Battle Camera Lock feature to cover the cinematic-camera path
        // alongside MsAtelRequestCamera and MsAtelRequestMagicCamera.
        //
        // Safety note: hook only Pause (not Free). Pause sets btl._24_1_=1 to enter
        // special mode; Free has an `if (btl._24_1_ != 0)` early guard so when we
        // suppress Pause, Free becomes a no-op naturally — no soft-lock risk.
        public const int MsBattleSpecialCameraPause = 0x0039ddd0;

        // MsBattleCameraTick — FUN_007be090 at FFX.exe+0x3BE090 (Ghidra VA 0x007be090).
        // The per-frame battle-camera APPLY driver, called once per frame from Sg_MainLoop.
        // Decomp-confirmed camera-only: it walks the shared `ms_camera` work-area slots
        // (MsCameraGetNum, world-matrix writes). The three Request hooks above only suppress
        // the camera *request queue*; scripted monster specials (e.g. Cactuar needles) write
        // the camera target directly via ATEL funcspace-6 opcodes / MsBattleSpecial self-math
        // into `ms_camera`, then this driver applies it — bypassing the request hooks. Hooking
        // and skip-orig'ing this while the Battle Camera Lock is engaged freezes the per-frame
        // apply so those scripted cameras cannot pan. Nullary `void(void)` → cc-safe.
        public const int MsBattleCameraTick = 0x003be090;

        // MsAtelRequestMagicCamera at FFX.exe+0x398010 (absolute 0x00798010) — sibling
        // of MsAtelRequestCamera, called from 6 sites during magic spell casts to
        // request the spell-specific camera animation. Bypasses MsAtelRequestCamera
        // entirely, so a separate hook is needed for the enemy-turn camera lock to
        // cover spell casts as well as normal attacks.
        //
        // Signature: byte (int p1, int p2, uint p3, int p4, int p5, int p6, uint p7, int p8, int p9)
        // Returns a byte camera-id; engine's "no camera" sentinel is 0xFF (engine line
        // 841782 default). Suppression branch must return 0xFF, not 0.
        public const int MsAtelRequestMagicCamera = 0x00398010;

        // MsBtlSetHitEffect at FFX.exe+0x39EC60 (absolute 0x0079EC60) — engine's
        // "registered hit effect" emitter. Global-handle sibling of 0x0039EBC0:
        // both route through the same dispatch core (FUN_0079EB30), but this one
        // resolves the effect ID against the global handle `btl._140_4_` rather
        // than the per-character handle `chr->field_0x4c`. That means the effect
        // table is universal across all actors — which is why it works on PC slots
        // where the per-character variant crashes.
        //
        // Signature: void (byte chr_id, int p2, int effect_id [, int extra]) —
        // same call shape as the per-character variant; the existing 4-arg delegate
        // continues to work.
        //
        // Script-side equivalent: opcode `Battle.btlSetHitEffReg [70E6h]`
        // (declared in target/audio/ath/btlatel.ath:234).
        //
        // Used to fire the Sentinel barrier visual (effect 0x4A) on the parrying
        // character on a successful parry.
        public const int MsBtlSetHitEffect = 0x0039ec60;

        // Effect-batch free/init pair (the engine uses MsEtEffectSet at battle start,
        // MsEtEffectStop at battle end). Calling Stop then Set mid-battle is a clean RESET:
        // op_et_battle_effect_free() clears all active hit effects, op_et_battle_effect_init()
        // re-arms the batch (+ re-points btl._140_4_) so the next MsBtlSetHitEffect is safe.
        // Stop WITHOUT the paired Set leaves the batch freed-but-uninitialised → next fire
        // crashes; always call them together. Used by the FX lab to stop/auto-clear previews.
        public const int MsEtEffectStop = 0x0039e7d0; // FUN_0079e7d0 (op_et_battle_effect_free)
        public const int MsEtEffectSet  = 0x0039e7c0; // FUN_0079e7c0 (op_et_battle_effect_init + repoint)

        // MsSetMotion — FUN_007ab380 at FFX.exe+0x3AB380 (Ghidra VA 0x007ab380). The
        // engine's battler motion setter. Signature (cdecl):
        //   undefined4 MsSetMotion(int slot, int motion_id, int chr_id, byte p4,
        //                          int p5, int p6, int p7)
        // Engine's own Defend code (MsDefenseStartProcess) calls it as
        //   MsSetMotion(slot, 0x3C|statusbit, 0, 0, 1, 0, 0)   // 0x3C/0x3D = guard brace
        //   MsSetMotion(slot, 0x34,           0, 0, 1, 0, 0)   // 0x34     = covered pose
        // i.e. the last two args are 0 (no context / no out-ptr) — that exact pattern is
        // the safe call shape used by the FX/Motion lab to preview arbitrary motion ids.
        public const int MsSetMotion = 0x003ab380;

        // MsInsertBtlCommand at FFX.exe+0x3929D0 — engine-public "queue a battle
        // command for a chr to execute as the next available action".
        //
        // Signature: int (AttackCue *cue, int param_2, int param_3, int chr_id)
        //   - cue: a fully-populated AttackCue (size 0x48 bytes — see
        //     Fahrenheit.FFX.Battle.AttackCue). Required fields:
        //       cue->attacker_id           = chr who will execute the command
        //       cue->command_count         = 1 (single command)
        //       cue->command_list[0].(*)   = command_ids[0] = command id (e.g.
        //         0x4000 for basic Attack); command_ids[1] = 0xFF sentinel;
        //         targets = bitmask of slot ids
        //   - param_2: 0 (unknown — always 0 in the engine's own usage)
        //   - param_3: 0 or 1 (other values rejected by an internal guard)
        //   - chr_id:  the target/context chr id (the slot the cue is "responding
        //     to"; engine's auto-counter passes the original incoming attacker)
        //   - returns 0 on success, -1 on validation failure.
        //
        // Discovered call pattern: see MsAutoRelifeProcess (FFX.exe+0x38C780) at
        // line 832786 in the decompile snapshot — that's the engine's own auto-
        // counter implementation. We follow the same convention for the streak
        // counter-attack feature so the cue interleaves cleanly with CTB.
        public const int MsInsertBtlCommand = 0x003929d0;

        // MsDmgCalc_CheckHit at FFX.exe+0x38A950 (absolute 0x0078A950) — engine's
        // accuracy/evasion roll inside the damage modifier pipeline. Returns
        // CheckHitResult enum (HIT / MISS / MISS_ALIVE). Called from MsDmgCalc only
        // (1 caller). Body reads command->flags_misc>>3&7 as hit_formula selector;
        // uses target.evasion vs user.accuracy plus aim/reflex/luck/jinx stacks.
        // Honors btl.debug.always_hit / never_hit. counter == EVADE_COUNTER forces
        // MISS (Counter / Reflex auto-ability path).
        //
        // Signature: int (Chr* user, Chr* target, Command* command, DamageInfo* info, int counter)
        //
        // Used by the disable-native-evasion feature to override MISS → HIT for PC
        // targets only (aeons and monsters fall through to vanilla evade).
        public const int MsDmgCalcCheckHit = 0x0038a950;
    }
}
