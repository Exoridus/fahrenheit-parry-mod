// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

// =============================================================================
// Custom-overdrive learning.
//
// This is gameplay, not diagnostics, and it compiles into Release. It lived in
// ParryModule.Debug.cs only because the save-data probe that discovered the
// offsets was written there first, and the learning logic grew next to it.
//
// The mod grants a character an Overdrive mode by decrementing that mode's learn
// counter in PlySave whenever the character parries — the engine's own counter,
// so the mode is learned through the game's normal rules rather than poked in.
// Reads and writes go through try_get_overdrive_learn_slots, which null-checks and
// bounds-checks every pointer: a wrong stride here corrupts a save file.
//
// Lifecycle: on_battle_session_begin() fires the read-only probe once per process
// and then the idempotent init. resolve_overdrive_learning_at_cue_clear() is called
// from the combat code when an enemy action ends, BEFORE the parry masks are
// cleared -- see ParryModule.Combat.cs.
// =============================================================================
public unsafe sealed partial class ParryModule
{
    // Set once per process by log_overdrive_modes_probe_once().
    private bool _saveDataOverdriveProbeFired;

    /// <summary>
    ///     The battle-begin edge: a live battle context exists for the first time since
    ///     the last one ended. Detected in <c>update_debug_battle_session_state()</c>,
    ///     which owns the edge; everything that needs to *happen* on it lives here.
    ///
    ///     The save structure is only populated once a battle context exists, so this is
    ///     the earliest safe point to read <c>limit_modes_obtained</c>. The read-only probe
    ///     runs first so a single log shows the before-state, then the init. The init only
    ///     ever writes the learn counter — never bit 17, never 0 — so firing it on every
    ///     battle is safe and idempotent for an already-armed character.
    /// </summary>
    private void on_battle_session_begin()
    {
        log_overdrive_modes_probe_once();
        apply_overdrive_learning_init();
    }

    /// <summary>
    ///     Read-only diagnostic: logs each permanent party character's
    ///     <c>limit_modes_obtained</c> bitmask (and the adjacent
    ///     <c>limit_mode_index</c>) so the derived <see cref="ExternalMemoryOffsetMap.SaveData"/>
    ///     offsets can be checked against the in-game Overdrive menu before any
    ///     write is considered.
    ///
    ///     <para>
    ///         Reads only — no writes anywhere. Each per-character read is wrapped so
    ///         a bad address logs a warning instead of taking down the game. Fires at
    ///         most once per process via <see cref="_saveDataOverdriveProbeFired"/>.
    ///         Bounded to the seven permanent playable members (char ids 0..6, the
    ///         set the mod already names) rather than the full name table, because
    ///         the <c>PlySave</c> array length past those entries is not verified and
    ///         a wrong stride would read unrelated memory.
    ///     </para>
    /// </summary>
    private void log_overdrive_modes_probe_once()
    {
        if (_saveDataOverdriveProbeFired) return;
        _saveDataOverdriveProbeFired = true;

        if (!_optionLogging) return;

        // char ids 0..6: Tidus, Yuna, Auron, Kimahri, Wakka, Lulu, Rikku.
        const int probeCharCount = 7;

        log_debug("[SaveProbe] Reading limit_modes_obtained (read-only; offsets DERIVED, verify vs Overdrive menu).");

        // Accumulates every character's learn counters so the closing summary line can
        // report min/median/max over the learnable ones (excludes learned=0 and n/a=0xFFFF).
        var allCounters = new List<short>(probeCharCount * ExternalMemoryOffsetMap.SaveData.LimitModeCounterCount);

        for (int charId = 0; charId < probeCharCount; charId++)
        {
            string name = try_map_party_chr_id_to_name(charId, out string resolved) ? resolved : "?";
            try
            {
                int entryRva = ExternalMemoryOffsetMap.SaveData.PlyArr0
                             + charId * ExternalMemoryOffsetMap.SaveData.PlySaveStride;

                uint* maskPtr  = FhUtil.ptr_at<uint>(entryRva + ExternalMemoryOffsetMap.SaveData.LimitModesObtained);
                byte* indexPtr = FhUtil.ptr_at<byte>(entryRva + ExternalMemoryOffsetMap.SaveData.LimitModeIndex);

                if (maskPtr == null || indexPtr == null)
                {
                    log_debug($"[SaveProbe] slot {charId} ({name}) — null pointer from ptr_at, read skipped.");
                    continue;
                }

                uint mask = *maskPtr;
                byte index = *indexPtr;

                log_debug(
                    $"[SaveProbe] slot {charId} ({name}) limit_modes_obtained=0x{mask:X8} "
                    + $"set_bits=[{OverdriveMaskFormatter.FormatSetBits(mask)}] limit_mode_index={index}");

                // limit_mode_counters: 20 shorts, each a per-mode learn countdown (start
                // value was the threshold, decrements per qualifying event). Read as short,
                // never written. 0 = learned, 0xFFFF (-1) = the character can never learn it.
                short* countersPtr = FhUtil.ptr_at<short>(entryRva + ExternalMemoryOffsetMap.SaveData.LimitModeCounters);
                if (countersPtr == null)
                {
                    log_debug($"[SaveProbe] slot {charId} ({name}) counters — null pointer from ptr_at, read skipped.");
                    continue;
                }

                var counterLine = new StringBuilder($"[SaveProbe] slot {charId} ({name}) counters:");
                for (int mode = 0; mode < ExternalMemoryOffsetMap.SaveData.LimitModeCounterCount; mode++)
                {
                    short raw = countersPtr[mode];
                    allCounters.Add(raw);
                    counterLine.Append(' ')
                        .Append(OverdriveCounterFormatter.ModeName(mode))
                        .Append('=')
                        .Append(OverdriveCounterFormatter.FormatValue(raw));
                }

                log_debug(counterLine.ToString());
            }
            catch (Exception ex)
            {
                log_debug($"[SaveProbe] slot {charId} ({name}) — read failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // One summary line: min/median/max across all characters' learnable counters — the
        // number the custom "learn by parrying N times" mode will be calibrated against.
        if (OverdriveCounterFormatter.TryComputeStats(allCounters, out int min, out double median, out int max))
        {
            log_debug(
                $"[SaveProbe] counter stats (learnable only, excludes learned=0 and n/a=0xFFFF): "
                + $"min={min} median={median.ToString("0.#", CultureInfo.InvariantCulture)} max={max} "
                + $"over {allCounters.Count} slots read.");
        }
        else
        {
            log_debug("[SaveProbe] counter stats: no learnable counters observed (all learned or n/a).");
        }
    }

    // ── Custom-overdrive "learn by parrying" (unconditional feature of the mod) ──
    //
    // The custom overdrive mode (index 0x11 / bit 17) is learned the way FFX teaches its own
    // modes: a per-character learn countdown in limit_mode_counters[0x11] that decrements per
    // successful parry and grants the mode (sets bit 17) when it reaches zero. The pure decision
    // policy lives in OverdriveLearnPolicy; this file is the save_ram I/O boundary.
    //
    // THIS WRITES INTO save_ram. Every write below goes into the live PlySave block: init writes
    // the per-character learn counter, counting decrements it, and the grant sets bit 17 of
    // limit_modes_obtained. If the player saves the game afterwards, the learn progress AND the
    // eventual unlock become permanent in that save file. This is intentional and always on.
    //
    // Char id set: 0..6 (Tidus, Yuna, Auron, Kimahri, Wakka, Lulu, Rikku) — the permanent
    // playable members, exactly the set the read-only SaveProbe enumerates. Summoned aeons
    // (chr_id >= 8) are skipped: they never appear in this counter path and the PlySave stride
    // past char 6 is not live-verified.
    //
    // Safety discipline:
    //   - Every read/write goes through FhUtil.ptr_at<T> with offset-map constants (rule §9), and
    //     is null-checked (try_get_overdrive_learn_slots) and bounds-checked before any write —
    //     these checks, not an opt-in setting, are what keep a bad offset off the player's save.
    //   - Never-zero-while-unset invariant: a grant sets bit 17 FIRST, then writes the counter
    //     to 0. Initialisation never writes 0. Counting never bare-decrements to 0.
    //   - Per-character try/catch: a failure logs a warning and cannot take down the game.
    private const int OverdriveLearnCharCount = 7;

    // Initialisation, at the battle-begin edge. Applies OverdriveLearnPolicy.DecideInitialisation
    // per character: arms an uninitialised (0xFFFF) or out-of-range counter to the threshold,
    // repairs the unsafe (counter 0 / bit unset) state with a warning, and leaves in-progress and
    // already-learned characters untouched.
    private void apply_overdrive_learning_init()
    {
        for (int charId = 0; charId < OverdriveLearnCharCount; charId++)
        {
            string name = try_map_party_chr_id_to_name(charId, out string resolved) ? resolved : "?";
            try
            {
                if (!try_get_overdrive_learn_slots(charId, out uint* maskPtr, out short* counterPtr))
                {
                    _logger.Warning($"[OverdriveLearn] slot {charId} ({name}) — null pointer from ptr_at, init skipped.");
                    continue;
                }

                bool bitSet = ((*maskPtr) & (1u << ExternalMemoryOffsetMap.SaveData.CustomOverdriveModeIndex)) != 0;
                short counter = *counterPtr;

                OverdriveLearnPolicy.InitDecision decision = OverdriveLearnPolicy.DecideInitialisation(counter, bitSet);
                switch (decision.Action)
                {
                    case OverdriveLearnPolicy.InitAction.Initialise:
                        *counterPtr = decision.WriteValue;
                        if (_optionLogging)
                            log_debug($"[OverdriveLearn] init slot {charId} ({name}) counter {counter} -> {decision.WriteValue} ({decision.Reason}).");
                        break;

                    case OverdriveLearnPolicy.InitAction.InitialiseWithWarning:
                        *counterPtr = decision.WriteValue;
                        _logger.Warning($"[OverdriveLearn] slot {charId} ({name}) — {decision.Reason} (counter {counter} -> {decision.WriteValue}).");
                        if (_optionLogging)
                            log_debug($"[OverdriveLearn] init slot {charId} ({name}) counter {counter} -> {decision.WriteValue} (WARN: {decision.Reason}).");
                        break;

                    default:
                        // NothingToDo / LeaveInProgress — no write.
                        if (_optionLogging)
                            log_debug($"[OverdriveLearn] init slot {charId} ({name}) — no change ({decision.Reason}).");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[OverdriveLearn] slot {charId} ({name}) — init failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Counting. Called once per enemy action window (cue-clear), reading the durable
    // LastParriedTargetMask BEFORE it is cleared, so a multi-hit attack the player parries counts
    // exactly once per character — matching the native counters, which are de-bounced to at most
    // one decrement per action window. A perfect dodge never sets this mask, so dodges are
    // excluded automatically (they charge the mode later, but do not teach it). Enemy attackers
    // and non-party slots are never in this party mask.
    private void resolve_overdrive_learning_at_cue_clear(uint parriedMask)
    {
        if (parriedMask == 0) return;

        Chr* party = _battleAdapter.GetPlayerCharacters();
        if (party == null) return;

        uint mask = parriedMask & PlayerTargetMask;
        while (mask != 0)
        {
            int slot = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;

            Chr* chr = party + slot;
            if (chr == null || !chr->stat_exist_flag) continue;

            // Map the battle slot to the character TEMPLATE id (chr_id @0xE), which indexes the
            // PlySave array — NOT the slot/id field @0xC. Only the permanent members 0..6 learn.
            int charId = chr->chr_id;
            if (charId < 0 || charId >= OverdriveLearnCharCount) continue;

            count_overdrive_parry_for_char(charId, slot);
        }
    }

    // Applies one de-bounced successful parry to a character's counter[0x11] via
    // OverdriveLearnPolicy.DecideParry. The grant path sets bit 17 first, then writes the counter
    // to 0 — the write ordering the never-zero-while-unset invariant depends on.
    private void count_overdrive_parry_for_char(int charId, int slot)
    {
        string name = try_map_party_chr_id_to_name(charId, out string resolved) ? resolved : "?";
        try
        {
            if (!try_get_overdrive_learn_slots(charId, out uint* maskPtr, out short* counterPtr))
            {
                _logger.Warning($"[OverdriveLearn] slot {charId} ({name}) — null pointer from ptr_at, parry count skipped.");
                return;
            }

            bool bitSet = ((*maskPtr) & (1u << ExternalMemoryOffsetMap.SaveData.CustomOverdriveModeIndex)) != 0;
            short counter = *counterPtr;

            OverdriveLearnPolicy.ParryDecision decision = OverdriveLearnPolicy.DecideParry(counter, bitSet);
            switch (decision.Action)
            {
                case OverdriveLearnPolicy.ParryAction.Grant:
                    // Order is load-bearing: bit 17 FIRST, then counter 0. Reversing it opens the
                    // window where MsLimitTypeProcess sees counter 0 with the bit unset and grants
                    // the mode incidentally.
                    *maskPtr = OverdriveMaskFormatter.WithModeBitSet(*maskPtr, ExternalMemoryOffsetMap.SaveData.CustomOverdriveModeIndex);
                    *counterPtr = decision.WriteCounterValue; // 0
                    if (_optionLogging)
                        log_debug($"[OverdriveLearn] GRANT slot {charId} ({name}) via {format_actor_slot((byte)slot)} — bit 17 set, counter -> 0 ({decision.Reason}).");
                    break;

                case OverdriveLearnPolicy.ParryAction.Decrement:
                    *counterPtr = decision.WriteCounterValue;
                    if (_optionLogging)
                        log_debug($"[OverdriveLearn] decrement slot {charId} ({name}) via {format_actor_slot((byte)slot)} — counter {counter} -> {decision.WriteCounterValue} ({decision.WriteCounterValue} remaining).");
                    break;

                default:
                    // AlreadyLearned / NotLearnable — no write.
                    if (_optionLogging)
                        log_debug($"[OverdriveLearn] slot {charId} ({name}) — no change ({decision.Reason}).");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[OverdriveLearn] slot {charId} ({name}) — parry count failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Resolves the mask + counter[0x11] pointers for one character's PlySave entry. Both go
    // through FhUtil.ptr_at<T> with offset-map constants (rule §9). counter[0x11] address:
    //   PlyArr0 + charId*PlySaveStride + LimitModeCounters (0x60) + 0x11*2 (0x22) = entry + 0x82.
    private bool try_get_overdrive_learn_slots(int charId, out uint* maskPtr, out short* counterPtr)
    {
        int entryRva = ExternalMemoryOffsetMap.SaveData.PlyArr0
                     + charId * ExternalMemoryOffsetMap.SaveData.PlySaveStride;

        maskPtr = FhUtil.ptr_at<uint>(entryRva + ExternalMemoryOffsetMap.SaveData.LimitModesObtained);
        short* countersBase = FhUtil.ptr_at<short>(entryRva + ExternalMemoryOffsetMap.SaveData.LimitModeCounters);
        counterPtr = countersBase != null
            ? countersBase + ExternalMemoryOffsetMap.SaveData.CustomOverdriveModeIndex
            : null;

        return maskPtr != null && counterPtr != null;
    }

    /// <summary>
    ///     Runtime gauge reward on a successful parry — distinct from the save-data learn
    ///     counters above, but the same domain, so it lives here rather than in the combat
    ///     resolution flow that calls it.
    /// </summary>
    private const float OverdriveBoostPercent = 0.05f;

    private void apply_overdrive_boost(uint mask)
    {
        if (!_optionOverdriveBoost) return;

        Chr* party = _battleAdapter.GetPlayerCharacters();
        if (party == null) return;

        uint effectiveMask = mask == 0 ? PlayerTargetMask : mask;

        for (int i = 0; i < PartyActorCapacity; i++)
        {
            uint bit = 1u << i;
            if ((effectiveMask & bit) == 0) continue;

            Chr* chr = party + i;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) continue;

            byte maxCharge = chr->ram.limit_charge_max;
            if (maxCharge == 0) continue;

            int before = chr->ram.limit_charge;
            uint delta = (uint)Math.Max(1, (int)MathF.Round(maxCharge * OverdriveBoostPercent));

            // Native charge primitive: clamps against limit_charge_max, honours the engine's
            // never_charge_overdrive debug flag, and applies Double/Triple Overdrive plus the
            // aura multipliers. Writing limit_charge directly bypassed all three.
            uint applied = FhUtil.get_fptr<MsLimitUpProbe>(
                ExternalMemoryOffsetMap.Functions.MsLimitUp)((uint)i, chr, delta);

            int after = chr->ram.limit_charge;
            if (after == before) continue;

            log_debug($"Increased overdrive for {format_actor_slot((byte)i)} from {before} to {after} (asked {delta}, applied {applied}).");
        }
    }
}
