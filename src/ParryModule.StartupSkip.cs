// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

// =============================================================================
// Bundled startup-skip convenience (ported from the standalone fahrenheit-nosplash
// / fahrenheit-nolauncher mods). Folded in here rather than maintained as separate
// mods; the hooks use the framework's FhMethodHandle<T> directly. Self-contained in
// this partial and always active (no parry setting gates it) — extract back into
// separate mods, or wire a setting, when convenient.
//
// NoSplash: redirect the splash/logo events to the title room (test20), suppress the
//           Japan-logo gate, and force-skip the boot Phyre FMVs.
// NoLauncher: suppress the engine relaunching FFX&X-2_LAUNCHER.exe on game close.
//
// Addresses are the ones the source mods used (verified working on this game build).
// =============================================================================
public unsafe sealed partial class ParryModule
{
    // ── startup-skip constants ───────────────────────────────────────────────
    private const ushort StartupSkipTitleRoomId     = 23;
    private const uint   StartupSkipMemochekEventId = 348;
    private const uint   StartupSkipLoopdemoEventId = 349;
    private const int    StartupForceRetryFrames    = 3;
    private const int    StartupForceMaxAttempts    = 120;
    private const ulong  BootSkipFrameWindow        = 300;  // ~10 sec @ 30 fps

    // Bundled convenience: always on for now. Wire to a setting if it ever needs gating.
    private readonly bool _startupSkipForceTitle = true;

    // ── delegates (AtelGetEventName is already declared on the main partial) ──
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StartupAtelEventSetUp(uint eventId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StartupNeedShowJapanLogo();

    // FUN_006d9590 — __thiscall void (FMVPlayerManager* this). Polls Triangle/Cross to
    // arm+commit a cutscene skip; hooked to force-skip Phyre FMVs in the boot window.
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void StartupFmvSkipPoll(nint thisPtr);

    // Win32 ShellExecuteW — __stdcall, returns HINSTANCE (>32 == success).
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate IntPtr StartupShellExecuteW(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpOperation,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpFile,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpParameters,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpDirectory,
        int nShowCmd);

    // ── hooks + state ────────────────────────────────────────────────────────
    private readonly FhMethodHandle<StartupAtelEventSetUp>    _hStartupAtelEventSetUp;
    private readonly FhMethodHandle<StartupNeedShowJapanLogo> _hStartupNeedShowJapanLogo;
    private readonly FhMethodHandle<StartupFmvSkipPoll>       _hStartupBootFmvSkip;
    private readonly FhMethodHandle<StartupShellExecuteW>     _hStartupShellExecuteW;

    private bool  _startupSkipStatusLogged;
    private int   _startupForceAttemptCount;
    private ulong _startupForceLastAttemptFrame;
    private int   _startupEventTraceCount;
    private long  _startupBootSkipFireCount;
    private long  _startupLauncherSuppressCount;

    // The four hook fields above are assigned in the ParryModule constructor
    // (ParryModule.cs), mirroring how the other production hooks are constructed there.

    // ── install (called from init) ───────────────────────────────────────────
    private void install_startup_skip_hooks()
    {
        try   { _hStartupAtelEventSetUp.hook(); }
        catch (Exception ex) { _logger.Warning($"[Parry][StartupSkip] Could not hook AtelEventSetUp (splash skip unavailable): {ex.Message}"); }

        try   { _hStartupNeedShowJapanLogo.hook(); }
        catch (Exception ex) { _logger.Warning($"[Parry][StartupSkip] Could not hook NeedShowJapanLogo (Japan logo skip reduced): {ex.Message}"); }

        try   { _hStartupBootFmvSkip.hook(); }
        catch (Exception ex) { _logger.Warning($"[Parry][StartupSkip] Could not hook FmvSkipPoll (boot FMV skip unavailable): {ex.Message}"); }

        try   { _hStartupShellExecuteW.hook(); }
        catch (Exception ex) { _logger.Warning($"[Parry][StartupSkip] Could not hook ShellExecuteW (launcher-relaunch suppression unavailable): {ex.Message}"); }

        _logger.Info("[Parry][StartupSkip] Splash/launcher skip ready.");
    }

    // ── per-frame tick (called from on_pre_update, before the parry-enabled gate) ──
    private void tick_startup_skip()
    {
        if (!_startupSkipForceTitle) return;

        if (!_startupSkipStatusLogged)
        {
            _startupSkipStatusLogged = true;
            int startupEventId = *FhFfx.Globals.event_id;
            _logger.Info($"[Parry][StartupSkip] Startup skip armed (event={startupEventId}).");
        }

        if (_debugFrameIndex < 10) return;
        if (startup_is_gameplay_ready()) return;

        int currentEventId = *FhFfx.Globals.event_id;
        string currentEventName = currentEventId > 0 ? startup_event_name((uint)currentEventId) : string.Empty;
        bool isSplash = startup_is_splash_event((uint)Math.Max(0, currentEventId), currentEventName);

        if (!isSplash) return;
        if (_startupForceAttemptCount >= StartupForceMaxAttempts) return;
        if (_startupForceLastAttemptFrame != 0
            && (_debugFrameIndex - _startupForceLastAttemptFrame) < StartupForceRetryFrames)
        {
            return;
        }

        bool redirected = false;
        try
        {
            _hStartupAtelEventSetUp.orig_fptr.Invoke(StartupSkipTitleRoomId);
            redirected = true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry][StartupSkip] Startup redirect call failed: {ex.Message}");
        }

        _startupForceLastAttemptFrame = _debugFrameIndex;
        if (redirected) _startupForceAttemptCount++;

        if (redirected || _startupForceAttemptCount <= 8 || (_startupForceAttemptCount % 10) == 0)
        {
            _logger.Info($"[Parry][StartupSkip] Startup skip forced (event={currentEventId}, name={currentEventName}, redirected={redirected}, attempts={_startupForceAttemptCount}).");
        }
    }

    // ── AtelEventSetUp hook: redirect splash events to the title room ─────────
    private void h_startup_event_setup(uint eventId)
    {
        if (_startupEventTraceCount < 8)
        {
            _startupEventTraceCount++;
            string traceName = startup_event_name(eventId);
            _logger.Info($"[Parry][StartupSkip] Startup event trace #{_startupEventTraceCount}: id={eventId}, name={traceName}.");
        }

        if (!_startupSkipForceTitle)
        {
            _hStartupAtelEventSetUp.orig_fptr.Invoke(eventId);
            return;
        }

        string eventName     = startup_event_name(eventId);
        uint   targetEventId  = eventId;

        if (startup_is_splash_event(eventId, eventName))
        {
            _logger.Info($"[Parry][StartupSkip] Startup redirect: {(string.IsNullOrWhiteSpace(eventName) ? "event" : eventName)} ({eventId}) -> test20 ({StartupSkipTitleRoomId}).");
            targetEventId = StartupSkipTitleRoomId;
        }

        _hStartupAtelEventSetUp.orig_fptr.Invoke(targetEventId);
    }

    // ── NeedShowJapanLogo hook: suppress the Japan-logo gate during startup ───
    private int h_startup_need_show_japan_logo()
    {
        if (_startupSkipForceTitle && !startup_is_gameplay_ready())
        {
            return 0;
        }

        return _hStartupNeedShowJapanLogo.orig_fptr.Invoke();
    }

    // ── Boot FMV skip hook — FUN_006d9590 / FFX.exe+0x002d9590 ────────────────
    // Short-circuits the engine's Triangle-to-skip poll during the first
    // BootSkipFrameWindow frames: if an FMV is actually playing, write the sentinels
    // the native skip-commit path writes and set the global skip flag, then return
    // without calling the original.
    private void h_startup_boot_fmv_skip(nint fmv)
    {
        if (_debugFrameIndex >= BootSkipFrameWindow)
        {
            _hStartupBootFmvSkip.orig_fptr.Invoke(fmv);
            return;
        }

        if (fmv == 0) return;

        byte* p = (byte*)fmv;

        // Guard: only act when the FMV manager reports it is actually playing.
        int* gMoviePlay = FhUtil.ptr_at<int>(StartupOffsets.GMoviePlay);
        if (gMoviePlay == null || *gMoviePlay != 1) { _hStartupBootFmvSkip.orig_fptr.Invoke(fmv); return; }
        if (p[0x6d0] == 0 || p[0x6d2] == 0)         { _hStartupBootFmvSkip.orig_fptr.Invoke(fmv); return; }

        // Write the sentinels the native skip-commit path writes.
        *(int*)(p + 0x6e0) = 0xfffe;
        *(int*)(p + 0x6d8) = 0xfffe;
        *(p + 0x710)       = 1;
        if (p[0x720] == 0) p[0x6d0] = 0;
        p[0x74c] = 0;

        // Set the global movie-skip flag (DAT_00cded21).
        byte* skipFlag = FhUtil.ptr_at<byte>(StartupOffsets.GMovieSkipFlag);
        if (skipFlag != null) *skipFlag = 1;

        long count = System.Threading.Interlocked.Increment(ref _startupBootSkipFireCount);
        _logger.Info($"[Parry][StartupSkip] Boot FMV skip fired (count={count}, frame={_debugFrameIndex}).");
        // Do NOT call original — we have committed the skip ourselves.
    }

    // ── ShellExecuteW hook: suppress the launcher relaunch on game close ──────
    private IntPtr h_startup_shell_execute_w(IntPtr hwnd, string? lpOperation, string? lpFile, string? lpParameters, string? lpDirectory, int nShowCmd)
    {
        if (lpFile != null && lpFile.EndsWith("LAUNCHER.exe", StringComparison.OrdinalIgnoreCase))
        {
            _startupLauncherSuppressCount++;
            _logger.Info($"[Parry][StartupSkip] Suppressed ShellExecuteW(\"{lpFile}\") on game close. (count={_startupLauncherSuppressCount})");
            // ShellExecuteW success convention: > 32. 33 is the canonical fake-success value.
            return (IntPtr)33;
        }
        return _hStartupShellExecuteW.orig_fptr.Invoke(hwnd, lpOperation, lpFile, lpParameters, lpDirectory, nShowCmd);
    }

    // ── event classification helpers ─────────────────────────────────────────
    private bool startup_is_gameplay_ready()
    {
        int eventId = *FhFfx.Globals.event_id;
        if (eventId <= 0) return false;

        string eventName = startup_event_name((uint)eventId);
        if (!startup_is_title_event((uint)eventId, eventName)
            && !startup_is_splash_event((uint)eventId, eventName))
        {
            return true;
        }

        byte* menuState = FhUtil.ptr_at<byte>(StartupOffsets.MenuState);
        return menuState != null && *menuState != 0;
    }

    private static bool startup_is_title_event(uint eventId, string eventName)
        => eventId == StartupSkipTitleRoomId
           || string.Equals(eventName, "test20", StringComparison.OrdinalIgnoreCase);

    private static bool startup_is_splash_event(uint eventId, string eventName)
        => eventId == StartupSkipMemochekEventId
           || eventId == StartupSkipLoopdemoEventId
           || string.Equals(eventName, "memochek", StringComparison.OrdinalIgnoreCase)
           || string.Equals(eventName, "loopdemo", StringComparison.OrdinalIgnoreCase);

    private static string startup_event_name(uint eventId)
    {
        try
        {
            char* ptr = FhUtil.get_fptr<AtelGetEventName>(ExternalMemoryOffsetMap.Functions.AtelGetEventName)(eventId);
            if (ptr == null) return string.Empty;
            return Marshal.PtrToStringAnsi((nint)ptr) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    // ── local offsets for the startup-skip feature ───────────────────────────
    private static class StartupOffsets
    {
        public const int AtelEventSetUp    = 0x00472e90;
        public const int NeedShowJapanLogo = 0x00387450;
        public const int MenuState         = 0x00F407E4;
        // FUN_006d9590: absolute 0x006d9590, FFX runtime offset = 0x002d9590
        public const int FmvSkipPoll       = 0x002d9590;
        // gMoviePlay: Ghidra VA 0x0112a008, FFX runtime offset = 0x00D2A008
        public const int GMoviePlay        = 0x00D2A008;
        // DAT_00cded21: address literal from decompilation, runtime offset = 0x008ded21
        public const int GMovieSkipFlag    = 0x008ded21;
    }
}
