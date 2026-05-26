namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap
{
    public static class ActorArray
    {
        // Actor array root data used for world actor ids/coords in many tooling projects.
        public const int ActorArraySize = 0x01FC44E0;
        public const int ActorArrayPointer = 0x01FC44E4;
        public const int ActorStride = 0x0880;

        // Per-actor offsets from actor base + ActorStride * index.
        public const int OffsetActorId = 0x0000;
        public const int OffsetPosX = 0x000C;
        public const int OffsetPosZ = 0x0010;
        public const int OffsetPosY = 0x0014;
    }

    /// <summary>
    ///     Per-Chr (battle character) struct offsets used by lethal-restore and
    ///     death-state correlation paths. Symbolic names instead of raw hex so
    ///     the death-latch fields can be cited and audited from one place.
    ///
    ///     Evidence trail (from LethalDiag PRE/POST log lines in
    ///     <c>h_ms_set_damage</c> and <c>h_ms_damage_set_motion</c>):
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <c>chr + 0xDCC</c> consistently reads <c>2</c> when the
    ///                 native pipeline has applied a lethal hit. Citation in
    ///                 <c>ParryModule.Hooks.cs</c>: "MsGetChrStatDeath returns 1
    ///                 when chr+0xDCC != 0, gating all downstream death
    ///                 processing." Cleared by lethal-restore to undo the death
    ///                 latch after HP is restored above 0.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>chr + 0x606</c> bit 0 reads <c>0x0001</c> on the same
    ///                 lethal-hit traces. Citation: "0x606 bit-0 is the
    ///                 dead-status bit ORed in by the same function." Cleared
    ///                 alongside <c>OffsetDeathLatch</c> to keep death from
    ///                 winning despite the HP restore.
    ///             </description>
    ///         </item>
    ///     </list>
    ///
    ///     The other diagnostic offsets (<c>0x700</c>, <c>0x702</c>, <c>0xDEE</c>,
    ///     <c>0xDD0</c>, <c>0xDD1</c>, <c>0xF5F</c>) are read for logging only
    ///     and have not been promoted; they remain raw in the diagnostic strings.
    /// </summary>
    public static class ChrStruct
    {
        /// <summary>
        ///     Death-latch byte. Read by <c>MsGetChrStatDeath</c>; non-zero gates
        ///     all downstream death processing (visual collapse, removal).
        ///     Cleared by the lethal-restore path after HP is restored above 0
        ///     so the death state cannot survive a successful parry's restore.
        /// </summary>
        public const int OffsetDeathLatch = 0xDCC;

        /// <summary>
        ///     Status-bits half-word. Bit 0 is the dead-status bit ORed in by
        ///     <c>MsGetChrStatDeath</c> when <see cref="OffsetDeathLatch"/> is
        ///     set. The lethal-restore path clears bit 0 explicitly.
        /// </summary>
        public const int OffsetStatusBits = 0x606;

        /// <summary>
        ///     Dead-status mask within the 16-bit field at
        ///     <see cref="OffsetStatusBits"/>. Cleared via
        ///     <c>value &amp;= ~DeadStatusBitMask</c>.
        /// </summary>
        public const ushort DeadStatusBitMask = 0x0001;

        /// <summary>
        ///     Confuse-status mask within the 16-bit field at
        ///     <see cref="OffsetStatusBits"/>. Battle traces show the Confuse
        ///     status can be staged here before <c>status_suffer</c> reflects it,
        ///     so the parry "non-parryable" check reads this bit as a fallback
        ///     ahead of the canonical flag.
        /// </summary>
        public const ushort ConfuseStatusBitMask = 0x0100;
    }
}
