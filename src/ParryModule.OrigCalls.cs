// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

// =============================================================================
// The alpha11 hook seam — migrated.
//
// Every hook body reaches the original native function through one of the private
// helpers below instead of touching a stored handle. On alpha11 `FhMethodHandle<T>`
// is a `ref struct`, so it can no longer be a class field: each helper constructs a
// fresh handle from a `FhMethodLocation` at the point of use and walks the rest of
// the chain via `chain_from(hook).fnptr`.
//
//   loc_xxx()   — builds the FhMethodLocation for a hooked function from this mod's
//                 own ExternalMemoryOffsetMap offsets (int, cast to nint). Startup
//                 hooks use StartupOffsets; ShellExecuteW targets a named export.
//   orig_xxx()  — builds a transient FhMethodHandle at loc_xxx(), retargets it past
//                 this mod's own hook (h_xxx) with chain_from, and invokes fnptr.
//   install_hook — shared installer: hook() returns bool on alpha11 (it does not
//                 throw), so each old try/catch collapses to one guarded line.
//
// Call sites in ParryModule.cs / ParryModule.Stage1Probes.cs / ParryModule.StartupSkip.cs
// are untouched by the migration: they already call orig_xxx(...), never a handle.
//
// Do not reference `FhCall.*` here: this mod deliberately no longer depends on that
// surface and builds its handles from ExternalMemoryOffsetMap offsets instead.
// =============================================================================
public unsafe sealed partial class ParryModule
{
    // ── shared installer ─────────────────────────────────────────────────────

    private void install_hook<T>(FhMethodLocation loc, T hook, string label) where T : Delegate
    {
        if (!new FhMethodHandle<T>(loc).hook(this, hook))
            _logger.Warning($"[Parry] Could not hook {label}.");
    }

    // ── ParryModule.cs (14 handles) ──────────────────────────────────────────

    private static FhMethodLocation loc_ms_exe_input_cue()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsExeInputCue);

    private static FhMethodLocation loc_ms_set_damage()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsSetDamage);

    private static FhMethodLocation loc_ms_damage_set_motion()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsDamageSetMotion);

    private static FhMethodLocation loc_ms_calc_damage()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsCalcDamage);

    private static FhMethodLocation loc_dmg_calc_armored()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.DmgCalcArmored);

    private static FhMethodLocation loc_ms_calc_damage_internal()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsCalcDamageInternal);

    private static FhMethodLocation loc_ms_set_damage_internal()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.DiscordCandidates.FnMsSetDamageInternal);

    private static FhMethodLocation loc_ms_atel_request_camera()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsAtelRequestCamera);

    private static FhMethodLocation loc_ms_atel_request_magic_camera()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsAtelRequestMagicCamera);

    private static FhMethodLocation loc_ms_battle_special_camera_pause()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsBattleSpecialCameraPause);

    private static FhMethodLocation loc_atel_camera_polar_set()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.AtelCameraPolarSet);

    private static FhMethodLocation loc_atel_camera_pos_set()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.AtelCameraPosSet);

    private static FhMethodLocation loc_ms_dmg_calc_check_hit()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsDmgCalcCheckHit);

    private static FhMethodLocation loc_ms_effect_end_motion()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsEffectEndMotion);

    private void orig_ms_exe_input_cue()
        => new FhMethodHandle<MsExeInputCueProbe>(loc_ms_exe_input_cue()).chain_from(h_ms_exe_input_cue).fnptr!();

    private int orig_ms_set_damage(byte param_1, int param_2, int param_3)
        => new FhMethodHandle<MsSetDamageProbe>(loc_ms_set_damage()).chain_from(h_ms_set_damage).fnptr!(param_1, param_2, param_3);

    private void orig_ms_damage_set_motion(byte target, int p2, int p3)
        => new FhMethodHandle<MsDamageSetMotionProbe>(loc_ms_damage_set_motion()).chain_from(h_ms_damage_set_motion).fnptr!(target, p2, p3);

    private int orig_ms_calc_damage(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
        => new FhMethodHandle<MsCalcDamageProbe>(loc_ms_calc_damage()).chain_from(h_ms_calc_damage).fnptr!(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

    private int orig_dmg_calc_armored(Chr* user, Chr* target, Command* command, int p4, int* p5, int damage)
        => new FhMethodHandle<DmgCalcArmoredProbe>(loc_dmg_calc_armored()).chain_from(h_dmg_calc_armored).fnptr!(user, target, command, p4, p5, damage);

    private int orig_ms_calc_damage_internal(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
        => new FhMethodHandle<MsCalcDamageInternalProbe>(loc_ms_calc_damage_internal()).chain_from(h_ms_calc_damage_internal).fnptr!(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

    private int orig_ms_set_damage_internal(int param_1, byte param_2, int param_3, int param_4, int param_5)
        => new FhMethodHandle<MsSetDamageInternalProbe>(loc_ms_set_damage_internal()).chain_from(h_ms_set_damage_internal).fnptr!(param_1, param_2, param_3, param_4, param_5);

    private int orig_ms_atel_request_camera(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8)
        => new FhMethodHandle<MsAtelRequestCameraProbe>(loc_ms_atel_request_camera()).chain_from(h_ms_atel_request_camera).fnptr!(p1, p2, p3, p4, p5, p6, p7, p8);

    private byte orig_ms_atel_request_magic_camera(int p1, int p2, uint p3, int p4, int p5, int p6, uint p7, int p8, int p9)
        => new FhMethodHandle<MsAtelRequestMagicCameraProbe>(loc_ms_atel_request_magic_camera()).chain_from(h_ms_atel_request_magic_camera).fnptr!(p1, p2, p3, p4, p5, p6, p7, p8, p9);

    private void orig_ms_battle_special_camera_pause(byte mode)
        => new FhMethodHandle<MsBattleSpecialCameraPauseProbe>(loc_ms_battle_special_camera_pause()).chain_from(h_ms_battle_special_camera_pause).fnptr!(mode);

    private int orig_atel_camera_polar_set(int worker, int p2, int stack, int isCam, int variant)
        => new FhMethodHandle<AtelCameraPolarSetProbe>(loc_atel_camera_polar_set()).chain_from(h_atel_camera_polar_set).fnptr!(worker, p2, stack, isCam, variant);

    private void orig_atel_camera_pos_set(int worker, int p2, int stack, int p4)
        => new FhMethodHandle<AtelCameraPosSetProbe>(loc_atel_camera_pos_set()).chain_from(h_atel_camera_pos_set).fnptr!(worker, p2, stack, p4);

    private int orig_ms_dmg_calc_check_hit(Chr* user, Chr* target, Command* command, void* info, int counter)
        => new FhMethodHandle<MsDmgCalcCheckHitProbe>(loc_ms_dmg_calc_check_hit()).chain_from(h_ms_dmg_calc_check_hit).fnptr!(user, target, command, info, counter);

    private void orig_ms_effect_end_motion(uint chr_id, int mode)
        => new FhMethodHandle<MsEffectEndMotionProbe>(loc_ms_effect_end_motion()).chain_from(h_ms_effect_end_motion).fnptr!(chr_id, mode);

    // ── ParryModule.StartupSkip.cs (4 handles) ───────────────────────────────

    private static FhMethodLocation loc_startup_atel_event_setup()
        => new("FFX.exe", (nint)StartupOffsets.AtelEventSetUp);

    private static FhMethodLocation loc_startup_need_show_japan_logo()
        => new("FFX.exe", (nint)StartupOffsets.NeedShowJapanLogo);

    private static FhMethodLocation loc_startup_boot_fmv_skip()
        => new("FFX.exe", (nint)StartupOffsets.FmvSkipPoll);

    private static FhMethodLocation loc_startup_shell_execute_w()
        => new("shell32.dll", "ShellExecuteW");

    private void orig_startup_atel_event_setup(uint eventId)
        => new FhMethodHandle<StartupAtelEventSetUp>(loc_startup_atel_event_setup()).chain_from(h_startup_event_setup).fnptr!(eventId);

    private int orig_startup_need_show_japan_logo()
        => new FhMethodHandle<StartupNeedShowJapanLogo>(loc_startup_need_show_japan_logo()).chain_from(h_startup_need_show_japan_logo).fnptr!();

    private void orig_startup_boot_fmv_skip(nint thisPtr)
        => new FhMethodHandle<StartupFmvSkipPoll>(loc_startup_boot_fmv_skip()).chain_from(h_startup_boot_fmv_skip).fnptr!(thisPtr);

    private IntPtr orig_startup_shell_execute_w(
        IntPtr hwnd,
        string? lpOperation,
        string? lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd)
        => new FhMethodHandle<StartupShellExecuteW>(loc_startup_shell_execute_w()).chain_from(h_startup_shell_execute_w).fnptr!(hwnd, lpOperation, lpFile, lpParameters, lpDirectory, nShowCmd);

    // ── ParryModule.Stage1Probes.cs (6 handles) ──────────────────────────────

    private static FhMethodLocation loc_stage1_ms_action_request()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsActionRequest);

    private static FhMethodLocation loc_stage1_ms_calc_command()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsCalcCommand);

    private static FhMethodLocation loc_stage1_ms_check_status_before_action()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsCheckStatusBeforeAction);

    private static FhMethodLocation loc_stage1_ms_limit_type_damage_check()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsLimitTypeDamageCheck);

    private static FhMethodLocation loc_stage1_op_et_battle_genko_counter_get()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.OpEtBattleGenkoCounterGet);

    private static FhMethodLocation loc_stage1_ms_set_motion()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsSetMotion);

    private uint orig_stage1_ms_action_request(int target_id, int attacker_id, int p3, int p4, int p5, int p6)
        => new FhMethodHandle<MsActionRequestProbeDelegate>(loc_stage1_ms_action_request()).chain_from(h_stage1_ms_action_request).fnptr!(target_id, attacker_id, p3, p4, p5, p6);

    private void orig_stage1_ms_calc_command()
        => new FhMethodHandle<MsCalcCommandProbeDelegate>(loc_stage1_ms_calc_command()).chain_from(h_stage1_ms_calc_command).fnptr!();

    private int orig_stage1_ms_check_status_before_action()
        => new FhMethodHandle<MsCheckStatusBeforeActionProbeDelegate>(loc_stage1_ms_check_status_before_action()).chain_from(h_stage1_ms_check_status_before_action).fnptr!();

    private int orig_stage1_ms_limit_type_damage_check(
        int attacker_id, nint attacker, int target_id, nint target, int p5, int p6, int p7)
        => new FhMethodHandle<MsLimitTypeDamageCheckProbeDelegate>(loc_stage1_ms_limit_type_damage_check()).chain_from(h_stage1_ms_limit_type_damage_check).fnptr!(
            attacker_id, attacker, target_id, target, p5, p6, p7);

    private int orig_stage1_op_et_battle_genko_counter_get()
        => new FhMethodHandle<OpEtBattleGenkoCounterGetProbeDelegate>(loc_stage1_op_et_battle_genko_counter_get()).chain_from(h_stage1_op_et_battle_genko_counter_get).fnptr!();

    private int orig_stage1_ms_set_motion(int p1, int p2, int chr_id, byte p4, int p5, int p6, int p7)
        => new FhMethodHandle<MsSetMotionProbeDelegate>(loc_stage1_ms_set_motion()).chain_from(h_stage1_ms_set_motion).fnptr!(p1, p2, chr_id, p4, p5, p6, p7);
}
