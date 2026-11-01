using System.Runtime.InteropServices;

namespace Fahrenheit.Mods.Parry;

public sealed partial class ParryModule
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsBtlSetHitEffectProbe(int chr_id, int p1, int effect_id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsCalcCommandProbeDelegate(nint cue, int commandIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsCheckStatusBeforeActionProbeDelegate(int actorId, nint actor);
}
