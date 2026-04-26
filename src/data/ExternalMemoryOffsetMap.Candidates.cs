namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap
{
    public static class DiscordCandidateOffsets
    {
        // Mentioned as queued/active command id in battle context: *(T_XCommandId*)((byte*)btl + 0x3A8)
        // Expected type: ushort
        public const int BtlOffsetLikelyQueuedCommandId = 0x03A8;
    }

    /// <summary>
    /// Candidate offsets/functions mentioned in Discord reverse-engineering threads.
    /// Treat as unverified until validated against runtime behavior in this project.
    /// </summary>
    public static class DiscordCandidates
    {
        // Mentioned in overdrive-mode checks in arena-related discussion.
        public const int BtlOffsetLikelyArenaContextFlag = 0x2115;

        // Mentioned in overdrive-learning flow discussion.
        public const int BtlOffsetLikelyOverdriveLearnPopupFlag = 0x175B;

        // Mentioned as active-monster count/state during monster init hooks.
        public const int GlobalLikelyInitializedMonsterCount = 0x00D2CA80;

        // Suggested range for monster/battle-related bytes in one debugging thread.
        public const int GlobalLikelyBattleRangeStart = 0x00D2CA90;
        public const int GlobalLikelyBattleRangeEnd = 0x00D33350;

        // Unvalidated function candidates from Discord (not yet promoted to Functions class).
        public const int FnMsSetDamageInternal = 0x0038F0B0;
        public const int FnEiAbmParaGet = 0x00A54860;

        // ── KB-sourced future-probe candidates ────────────────────────────────
        //
        // Names sourced from the ffx-knowledge-base repo (specifically
        // canonical/ffx/research/engine_hook_candidates.json + the curated
        // inputs/mappings/function_renames.json — provenance =
        // ghidra-server:ffx-v3 unless noted otherwise). NOT in the upstream
        // FhFfx.FhCall.__addr_* table at the time of writing — added here
        // so the KB probe-plan / engine-hook-callgraph can be wired in a
        // future PR without re-resolving each address.
        //
        // No production hook references these constants yet. Stage-1 observe
        // probes (per docs/fahrenheit-parry-probe-plan.md in the KB) are the
        // intended consumer.
        //
        // If an upstream call.g.cs adds any of these under __addr_*, prefer
        // the upstream reference and remove the local constant here.

        // High-confidence-inferred via the combat-seed (matches structural
        // pattern of MsSubHP / MsSubCTB). Companion HP/CTB sub-functions for
        // the MP and CTB damage-commit fields.
        public const int FnMsSubMP  = 0x0038E400;
        public const int FnMsSubCTB = 0x0038E2A0;

        // ghidra-server:ffx-v3 imported. Post-action status gate; brackets
        // MsCheckStatusBeforeAction (which IS in __addr_* upstream).
        public const int FnMsCheckStatusAfterAction = 0x003AF3A0;

        // ghidra-server:ffx-v3 imported. Battle camera rect setter; relevant
        // for the camera-suppression feasibility track in the KB review.
        public const int FnMsCameraSetRect = 0x003BF8C0;

        // Mentioned as FFX input raw address in Fahrenheit-dev discussion.
        public const int InputRawAddress = 0x00F27080;

        // Mentioned global candidate in Fahrenheit-dev discussion.
        public const int GlobalCandidateD35DF8 = 0x00D35DF8;
    }
}
