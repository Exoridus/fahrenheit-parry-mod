namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private void render_setting_enabled()
    {
        if (ImGui.Checkbox("##fhparry.enabled", ref _optionEnabled))
        {
            log_debug($"Master toggle changed: {_optionEnabled}.");
            if (!_optionEnabled)
            {
                reset_runtime_state("disabled_setting", clearFeedbackFlashes: true, clearDamageFlags: true);
                log_debug("Disabled while active; runtime state reset.");
            }
        }
    }

    private void render_setting_audio()
    {
        if (ImGui.Checkbox("##fhparry.audio", ref _optionSound) && !_optionSound)
        {
            stop_audio_playback();
        }
    }

    private void render_setting_audio_volume()
    {
        if (!_optionSound)
        {
            ImGui.BeginDisabled(true);
        }

        ImGui.SliderFloat("##fhparry.audio_volume", ref _optionAudioVolume, 0f, 1f, "%.2f");

        if (!_optionSound)
        {
            ImGui.EndDisabled();
        }
    }

    private void render_setting_startup_skip()
    {
        if (ImGui.Checkbox("##fhparry.startup_skip", ref _optionStartupSkipForceTitle))
        {
            _startupForceAttemptCount = 0;
            _startupForceLastAttemptFrame = 0;
            _startupTest20PatchApplied = false;
            _startupTest20PatchMismatchLogged = false;
            if (_optionStartupSkipForceTitle)
            {
                log_debug("Startup force-title skip enabled.");
            }
            else
            {
                log_debug("Startup force-title skip disabled.");
            }
        }
    }

    private void render_setting_overdrive_boost()
    {
        ImGui.Checkbox("##fhparry.ctb", ref _optionOverdriveBoost);
    }

    private void render_setting_negate()
    {
        ImGui.Checkbox("##fhparry.negate", ref _optionNegateDamage);
    }

    private void render_setting_logging()
    {
        if (ImGui.Checkbox("##fhparry.logging", ref _optionLogging))
        {
            string state = _optionLogging ? "enabled" : "disabled";
            _logger.Info($"[Parry] Debug logging {state} via settings.");
        }
    }

    private void render_setting_debug_overlay()
    {
        if (ImGui.Checkbox("##fhparry.debug_overlay", ref _optionDebugOverlay))
        {
            string state = _optionDebugOverlay ? "enabled" : "disabled";
            log_debug($"Debug overlay {state}.");
        }
    }

    private void render_setting_difficulty()
    {
        int idx = Math.Clamp((int)_optionDifficulty, 0, 2);
        if (ImGui.Combo("##fhparry.difficulty", ref idx, "Easy\0Normal\0Expert\0"))
        {
            _optionDifficulty = idx switch
            {
                0 => ParryDifficulty.Easy,
                2 => ParryDifficulty.Expert,
                _ => ParryDifficulty.Normal
            };
            reset_spam_tier("difficulty_changed", logTransition: true);
            log_debug($"Difficulty changed to {ParryDifficultyModel.FormatName(_optionDifficulty)}.");
        }
    }

    private void render_setting_future()
    {
        ImGui.BeginDisabled(true);
        ImGui.TextWrapped("Auto-counter customization coming soon.");
        ImGui.EndDisabled();
    }
}
