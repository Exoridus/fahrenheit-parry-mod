using ParryPeer = Fahrenheit.Mods.Parry.ParryModule;

namespace Fahrenheit.Mods.Vrr;

// Native admission and snapshot boundaries are synthetic; binding and dispatch are production code.
public unsafe sealed partial class VrrModule
{
    private readonly record struct PadSnapshotScope(long Invocation, bool Owned, bool Root);
    private const int PadSnapshotContinuation = 1;
    private readonly PresentationAdmission _presentationAdmission = new();
    private bool RuntimeEffectsArmed => true;
    private bool IndependentPresentationEnabled => true;
    private bool RenderSchedulerReady => true;
    private bool IsOwnedRenderLoop => _render_inside_loop;
    private bool _render_frame_enabled = true, _render_inside_loop, _render_clock_seen,
        _render_simulation_pass, _padReplayApplied, _padPublicationNativeFallback = false,
        _padSnapshotClockCompleted = false;
    private int _render_owner_thread = Environment.CurrentManagedThreadId,
        _padSnapshotOwner, _padSnapshotLoopDepth, _padSnapshotProducerDepth = 0;
    private long _padSnapshotInvocation, _padSnapshotEpoch;
    private bool _nativeOwner;
    private bool render_scheduler_frame_eligible() => true;
    private bool pad_native_input_owner() => _nativeOwner;
    private void diagnostic_event(string name, object value) { }

    public VrrModule(int simulationHz)
    {
        TargetFramerate = simulationHz;
        RenderPassContext.IsActive = true;
        RenderPassContext.IsPresentation = false;
        RenderPassContext.ResetHistory();
    }

    private int TargetFramerate { get; }
    public bool Bind(ParryPeer parry) => bind_cooperative_ticks([parry]);
    public bool Ready => cooperative_ticks_ready();
    public string? Failure => _presentationAdmission.Failure;
    public long Epoch => RenderPassContext.HistoryEpoch;
    public long Serial => RenderPassContext.SimulationSerial;
    public void ResetHistory() => RenderPassContext.ResetHistory();
    public void BeginImage(double seconds) => RenderPassContext.BeginImage(seconds);

    public void RunLoop(ParryPeer parry, bool simulation, float delta, bool nativeHandover = false)
    {
        EnterLoop();
        parry.PreUpdate(delta);
        CompleteClock(simulation);
        if (nativeHandover)
        {
            _render_frame_enabled = false;
            _nativeOwner = true;
            RenderPassContext.ResetHistory();
        }
        if (simulation) PublishSnapshot(applied: !nativeHandover);
        ExitLoop();
        _render_frame_enabled = true;
        _nativeOwner = false;
    }

    public void EnterLoop()
    {
        _render_clock_seen = false;
        _padSnapshotInvocation++;
        _padSnapshotLoopDepth = 1;
        _padSnapshotOwner = Environment.CurrentManagedThreadId;
        _padSnapshotEpoch = RenderPassContext.HistoryEpoch;
        cooperative_tick_enter_loop(new(_padSnapshotInvocation, true, true));
        _render_inside_loop = true;
    }

    public void CompleteClock(bool simulation)
    {
        _render_clock_seen = true;
        _render_simulation_pass = simulation;
        RenderPassContext.IsPresentation = !simulation;
        if (simulation) RenderPassContext.BeginSimulation();
        cooperative_tick_clock_completed(true);
    }

    public void PublishSnapshot(bool applied = true)
    {
        _padReplayApplied = applied;
        cooperative_tick_snapshot_completed((nuint)FhUtil.ptr_at<byte>(PadSnapshotContinuation), applied);
    }

    public void ExitLoop()
    {
        cooperative_tick_exit_loop(new(_padSnapshotInvocation, true, true));
        _render_inside_loop = false;
    }

    public void ReplayIdentity(long epoch, long serial, float delta) => _cooperativeTick!(epoch, serial, delta);
}

internal static unsafe class FhUtil
{
    internal static T* ptr_at<T>(int marker) where T : unmanaged => (T*)marker;
}
