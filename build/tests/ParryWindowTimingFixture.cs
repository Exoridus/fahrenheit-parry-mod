namespace Fahrenheit.Mods.Parry;

public sealed partial class ParryModule
{
    private const int PartyActorCapacity = 10;
    private readonly ulong[] _parryExpiryTick = new ulong[PartyActorCapacity];
    private readonly byte[] _parryArmedAttackerId = new byte[PartyActorCapacity];
    private readonly TimingRuntime _runtime = new();
    private ulong _debugFrameIndex = 100;

    private sealed class TimingRuntime
    {
        public ParryInputState InputState;
        public bool ParryWindowActive;
        public int ParryWindowElapsedTicks, ParryWindowRemainingTicks, WhiffLockoutRemainingTicks;
        public byte CurrentAttackerId = 22;
        public ulong WindowOpenFrame;
    }

    public ParryInputState WindowState => _runtime.InputState;
    public int Whiffs { get; private set; }
    public void OpenWindow(int ticks, uint targets = 1)
    {
        _runtime.InputState = ParryInputState.Open;
        _runtime.ParryWindowActive = true;
        _runtime.WindowOpenFrame = _debugFrameIndex;
        _runtime.ParryWindowRemainingTicks = ticks;
        arm_parry_slot_windows(targets, ticks);
    }

    public bool WindowAccepts(int slot = 0) => is_parry_slot_window_open(slot);
    public void TickWindow(bool advance = true)
    {
        if (advance) _debugFrameIndex++;
        tick_parry_window();
    }
    public void ResolveWindow() => _runtime.InputState = ParryInputState.Resolved;
    public void ClearWindow() => clear_parry_slot_windows();
    public void ResetWindow()
    {
        clear_parry_slot_windows();
        _runtime.InputState = ParryInputState.Ready;
        _runtime.ParryWindowActive = false;
    }
    public bool PressWindow(int ticks)
    {
        if (!ParryInputStateTransitions.DecidePress(_runtime.InputState, true).Accepted) return false;
        OpenWindow(ticks);
        return true;
    }
    private void transition_to_whiff_lockout()
    {
        Whiffs++;
        _runtime.InputState = ParryInputState.WhiffLockout;
        _runtime.WhiffLockoutRemainingTicks = 3;
        _runtime.ParryWindowActive = false;
        clear_parry_slot_windows();
    }
    private void transition_whiff_lockout_to_ready() => _runtime.InputState = ParryInputState.Ready;
}
