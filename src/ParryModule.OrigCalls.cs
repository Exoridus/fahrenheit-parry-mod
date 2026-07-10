// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

// =============================================================================
// The alpha11 migration seam.
//
// Every hook body calls the original native function through one of the private
// helpers below instead of touching `_hXxx.orig_fptr` directly. On today's alpha10,
// each helper is a one-line forward: `_hXxx.orig_fptr.Invoke(...)`.
//
// On alpha11, `FhMethodHandle<T>` becomes a `ref struct` (so it can no longer live
// as a class field) and `orig_fptr` is replaced by `chain_from(hook).fnptr`. When
// that migration lands, ONLY the bodies in this file change — every call site in
// ParryModule.Hooks.cs / ParryModule.Stage1Probes.cs / ParryModule.StartupSkip.cs
// stays untouched, because they already call `orig_xxx(...)`, not `_hXxx.orig_fptr`.
//
// Do not reference `FhCall.*` here: upstream is renaming `FhCall.h_METHOD` to
// `FhCall.METHOD` ahead of alpha11, so the migrated handles will be built from this
// mod's own `ExternalMemoryOffsetMap` offsets instead of `FhCall` constants.
// =============================================================================
public unsafe sealed partial class ParryModule
{
    // ── ParryModule.cs (14 handles) ──────────────────────────────────────────

    private void orig_ms_exe_input_cue()
        => _hMsExeInputCue.orig_fptr.Invoke();

    private int orig_ms_set_damage(byte param_1, int param_2, int param_3)
        => _hMsSetDamage.orig_fptr.Invoke(param_1, param_2, param_3);

    private void orig_ms_damage_set_motion(byte target, int p2, int p3)
        => _hMsDamageSetMotion.orig_fptr.Invoke(target, p2, p3);

    private int orig_ms_calc_damage(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
        => _hMsCalcDamage.orig_fptr.Invoke(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

    private int orig_dmg_calc_armored(Chr* user, Chr* target, Command* command, int p4, int* p5, int damage)
        => _hDmgCalcArmored.orig_fptr.Invoke(user, target, command, p4, p5, damage);

    private int orig_ms_calc_damage_internal(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
        => _hMsCalcDamageInternal.orig_fptr.Invoke(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

    private int orig_ms_set_damage_internal(int param_1, byte param_2, int param_3, int param_4, int param_5)
        => _hMsSetDamageInternal.orig_fptr.Invoke(param_1, param_2, param_3, param_4, param_5);

    private int orig_ms_atel_request_camera(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8)
        => _hMsAtelRequestCamera.orig_fptr.Invoke(p1, p2, p3, p4, p5, p6, p7, p8);

    private byte orig_ms_atel_request_magic_camera(int p1, int p2, uint p3, int p4, int p5, int p6, uint p7, int p8, int p9)
        => _hMsAtelRequestMagicCamera.orig_fptr.Invoke(p1, p2, p3, p4, p5, p6, p7, p8, p9);

    private void orig_ms_battle_special_camera_pause(byte mode)
        => _hMsBattleSpecialCameraPause.orig_fptr.Invoke(mode);

    private int orig_atel_camera_polar_set(int worker, int p2, int stack, int isCam, int variant)
        => _hAtelCameraPolarSet.orig_fptr.Invoke(worker, p2, stack, isCam, variant);

    private void orig_atel_camera_pos_set(int worker, int p2, int stack, int p4)
        => _hAtelCameraPosSet.orig_fptr.Invoke(worker, p2, stack, p4);

    private int orig_ms_dmg_calc_check_hit(Chr* user, Chr* target, Command* command, void* info, int counter)
        => _hMsDmgCalcCheckHit.orig_fptr.Invoke(user, target, command, info, counter);

    private void orig_ms_effect_end_motion(uint chr_id, int mode)
        => _hMsEffectEndMotion.orig_fptr.Invoke(chr_id, mode);

    // ── ParryModule.StartupSkip.cs (4 handles) ───────────────────────────────

    private void orig_startup_atel_event_setup(uint eventId)
        => _hStartupAtelEventSetUp.orig_fptr.Invoke(eventId);

    private int orig_startup_need_show_japan_logo()
        => _hStartupNeedShowJapanLogo.orig_fptr.Invoke();

    private void orig_startup_boot_fmv_skip(nint thisPtr)
        => _hStartupBootFmvSkip.orig_fptr.Invoke(thisPtr);

    private IntPtr orig_startup_shell_execute_w(
        IntPtr hwnd,
        string? lpOperation,
        string? lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd)
        => _hStartupShellExecuteW.orig_fptr.Invoke(hwnd, lpOperation, lpFile, lpParameters, lpDirectory, nShowCmd);

    // ── ParryModule.Stage1Probes.cs (6 nullable handles) ─────────────────────

    private uint orig_stage1_ms_action_request(int target_id, int attacker_id, int p3, int p4, int p5, int p6)
        => _hStage1MsActionRequest!.orig_fptr.Invoke(target_id, attacker_id, p3, p4, p5, p6);

    private void orig_stage1_ms_calc_command()
        => _hStage1MsCalcCommand!.orig_fptr.Invoke();

    private int orig_stage1_ms_check_status_before_action()
        => _hStage1MsCheckStatusBeforeAction!.orig_fptr.Invoke();

    private int orig_stage1_ms_limit_type_damage_check(
        int attacker_id, nint attacker, int target_id, nint target, int p5, int p6, int p7)
        => _hStage1MsLimitTypeDamageCheck!.orig_fptr.Invoke(
            attacker_id, attacker, target_id, target, p5, p6, p7);

    private int orig_stage1_op_et_battle_genko_counter_get()
        => _hStage1OpEtBattleGenkoCounterGet!.orig_fptr.Invoke();

    private int orig_stage1_ms_set_motion(int p1, int p2, int chr_id, byte p4, int p5, int p6, int p7)
        => _hStage1MsSetMotion!.orig_fptr.Invoke(p1, p2, chr_id, p4, p5, p6, p7);
}
