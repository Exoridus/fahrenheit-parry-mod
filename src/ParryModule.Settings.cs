namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private void render_setting_enabled()
    {
        if (ImGui.Checkbox("##fhparry.enabled", ref _optionEnabled))
        {
            persist_settings();
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
        if (ImGui.Checkbox("##fhparry.audio", ref _optionSound))
        {
            persist_settings();
            if (!_optionSound)
            {
                stop_audio_playback();
            }
        }
    }

    private void render_setting_audio_volume()
    {
        if (!_optionSound)
        {
            ImGui.BeginDisabled(true);
        }

        bool changed = ImGui.SliderFloat("##fhparry.audio_volume", ref _optionAudioVolume, 0f, 1f, "%.2f");
        _optionAudioVolume = Math.Clamp(_optionAudioVolume, 0f, 1f);
        if (changed && ImGui.IsItemDeactivatedAfterEdit())
        {
            persist_settings();
        }

        if (!_optionSound)
        {
            ImGui.EndDisabled();
        }
    }

    private void render_setting_parry_state_hud()
    {
        if (ImGui.Checkbox("##fhparry.parry_state_hud", ref _optionParryStateHud))
        {
            persist_settings();
        }
    }

    private void render_setting_startup_skip()
    {
        if (ImGui.Checkbox("##fhparry.startup_skip", ref _optionStartupSkipForceTitle))
        {
            persist_settings();
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
        if (ImGui.Checkbox("##fhparry.ctb", ref _optionOverdriveBoost))
        {
            persist_settings();
        }
    }

    private void render_setting_negate()
    {
        if (ImGui.Checkbox("##fhparry.negate", ref _optionNegateDamage))
        {
            persist_settings();
        }
    }

    private void render_setting_penalty()
    {
        if (ImGui.Checkbox("##fhparry.penalty", ref _optionPenaltyEnabled))
        {
            persist_settings();
            if (!_optionPenaltyEnabled)
            {
                reset_spam_tier("penalty_disabled", logTransition: true);
            }
        }
    }

    private void render_setting_logging()
    {
        if (ImGui.Checkbox("##fhparry.logging", ref _optionLogging))
        {
            persist_settings();
            string state = _optionLogging ? "enabled" : "disabled";
            _logger.Info($"[Parry] Debug logging {state} via settings.");
        }
    }

    private void render_setting_debug_overlay()
    {
        if (ImGui.Checkbox("##fhparry.debug_overlay", ref _optionDebugOverlay))
        {
            persist_settings();
            string state = _optionDebugOverlay ? "enabled" : "disabled";
            log_debug($"Debug overlay {state}.");
        }
    }

    private void render_setting_difficulty()
    {
        int idx = Math.Clamp((int)_optionDifficulty, 0, 3);
        if (ImGui.Combo("##fhparry.difficulty", ref idx, "Easy\0Normal\0Expert\0Debug\0"))
        {
            _optionDifficulty = idx switch
            {
                0 => ParryDifficulty.Easy,
                2 => ParryDifficulty.Expert,
                3 => ParryDifficulty.Debug,
                _ => ParryDifficulty.Normal
            };
            persist_settings();
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
