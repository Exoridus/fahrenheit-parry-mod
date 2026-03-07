namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private void update_startup_probe()
    {
        if (!_optionStartupProbeMode || _sessionLogDisabled || _startupProbeCompleted)
        {
            return;
        }

        FhFfx.SaveData* save = FhFfx.Globals.save_data;
        bool gameplayReadyOverlay = is_gameplay_ready_for_overlay(save);
        bool gameplayReadyStartup = is_gameplay_ready_for_startup_skip();
        bool battleActive = _battleAdapter.GetBattle() != null;

        if (_simulationClockSeconds > StartupProbeWindowSeconds && gameplayReadyOverlay)
        {
            write_session_startup_probe_entry(
                timestampLocal: current_gameplay_timestamp(),
                frameIndex: _debugFrameIndex,
                reason: "probe_end",
                eventId: *FhFfx.Globals.event_id,
                eventName: get_current_event_name_safe((uint)Math.Max(0, *FhFfx.Globals.event_id)),
                menuState: read_u8_at(0xF407E4),
                needAutoSave: read_u32_ptr(FhFfx.FhCall.gNeedAutoSave),
                moviePlay: read_u32_at(0xD2A008),
                stateD36FA0: read_u32_at(0xD36FA0),
                stateD36FA4: read_u32_at(0xD36FA4),
                saveData0C88: read_save_data_byte(save, StartupSkipProgressFlagOffset),
                gameplayReadyOverlay: gameplayReadyOverlay,
                gameplayReadyStartup: gameplayReadyStartup,
                battleActive: battleActive);

            _startupProbeCompleted = true;
            _logger.Info("[Parry] Startup probe completed.");
            return;
        }

        int eventId = *FhFfx.Globals.event_id;
        string eventName = get_current_event_name_safe((uint)Math.Max(0, eventId));
        int menuState = read_u8_at(0xF407E4);
        int needAutoSave = read_u32_ptr(FhFfx.FhCall.gNeedAutoSave);
        int moviePlay = read_u32_at(0xD2A008);
        int stateD36FA0 = read_u32_at(0xD36FA0);
        int stateD36FA4 = read_u32_at(0xD36FA4);
        int saveData0C88 = read_save_data_byte(save, StartupSkipProgressFlagOffset);

        string signature =
            $"{eventId}|{eventName}|{menuState}|{needAutoSave}|{moviePlay}|{stateD36FA0}|{stateD36FA4}|{saveData0C88}|{(gameplayReadyOverlay ? 1 : 0)}|{(gameplayReadyStartup ? 1 : 0)}|{(battleActive ? 1 : 0)}";

        bool changed = !string.Equals(signature, _startupProbeLastSignature, StringComparison.Ordinal);
        bool periodic = _startupProbeLastFrame == 0 || (_debugFrameIndex - _startupProbeLastFrame) >= StartupProbePeriodicFrames;
        if (!changed && !periodic)
        {
            return;
        }

        write_session_startup_probe_entry(
            timestampLocal: current_gameplay_timestamp(),
            frameIndex: _debugFrameIndex,
            reason: changed ? "change" : "sample",
            eventId: eventId,
            eventName: eventName,
            menuState: menuState,
            needAutoSave: needAutoSave,
            moviePlay: moviePlay,
            stateD36FA0: stateD36FA0,
            stateD36FA4: stateD36FA4,
            saveData0C88: saveData0C88,
            gameplayReadyOverlay: gameplayReadyOverlay,
            gameplayReadyStartup: gameplayReadyStartup,
            battleActive: battleActive);

        _startupProbeLastFrame = _debugFrameIndex;
        if (changed)
        {
            _startupProbeLastSignature = signature;
        }
    }

    private static int read_u8_at(nint address)
    {
        try
        {
            byte* ptr = FhUtil.ptr_at<byte>(address);
            return ptr == null ? -1 : *ptr;
        }
        catch
        {
            return -1;
        }
    }

    private static int read_u32_at(nint address)
    {
        try
        {
            uint* ptr = FhUtil.ptr_at<uint>(address);
            return ptr == null ? -1 : unchecked((int)(*ptr));
        }
        catch
        {
            return -1;
        }
    }

    private static int read_u32_ptr(uint* ptr)
    {
        try
        {
            return ptr == null ? -1 : unchecked((int)(*ptr));
        }
        catch
        {
            return -1;
        }
    }

    private static int read_save_data_byte(FhFfx.SaveData* save, int offset)
    {
        if (save == null) return -1;

        try
        {
            return ((byte*)save)[offset];
        }
        catch
        {
            return -1;
        }
    }

    private static string get_current_event_name_safe(uint eventId)
    {
        if (eventId == 0) return string.Empty;
        return get_current_event_name(eventId);
    }
}
