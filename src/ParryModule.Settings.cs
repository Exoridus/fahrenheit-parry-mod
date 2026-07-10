namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    /// <summary>
    ///     Draws one setting: its localized name, then the widget indented beneath it,
    ///     with the localized description as a hover tooltip.
    ///
    ///     The widgets themselves use hidden labels (`##fhparry.x`) because Fahrenheit's
    ///     settings panel used to draw the name and description for us. It no longer can:
    ///     `FhSettingCustomRenderer` is gone on alpha11 and the replacement surface offers
    ///     no boolean or combo type. So we draw the chrome ourselves, from the same
    ///     `fhparry.<id>.name` / `.desc` keys in lang/*.json.
    ///
    ///     The name is drawn above rather than beside the widget because several renderers
    ///     emit more than one line (the camera-lock combo trails a TextDisabled note), and
    ///     ImGui.SameLine would attach the label to whatever they drew last.
    /// </summary>
    private static void setting_row(string id, Action widget)
    {
        ImGui.TextUnformatted(FhApi.Localization.localize($"fhparry.{id}.name"));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(FhApi.Localization.localize($"fhparry.{id}.desc"));
        }

        ImGui.Indent();
        widget();
        ImGui.Unindent();
        ImGui.Spacing();
    }

    /// <summary>
    ///     The Settings tab. Replaces the FhSettingsCategory registration that alpha11
    ///     removed. Persistence is unaffected — the mod has always written its own
    ///     fhparry.config.json.
    /// </summary>
    private void render_settings_tab()
    {
        ImGui.SeparatorText("Core");
        setting_row("enabled",    render_setting_enabled);
        setting_row("difficulty", render_setting_difficulty);

        ImGui.SeparatorText("Dodge");
        setting_row("dodge_window",        render_setting_dodge_window);
        setting_row("dodge_whiffout",      render_setting_dodge_whiffout);
        setting_row("dodge_motion_cancel", render_setting_dodge_motion_cancel);

        ImGui.SeparatorText("Reward");
        setting_row("ctb",            render_setting_overdrive_boost);
        setting_row("streak_counter", render_setting_streak_counter);
        setting_row("penalty",        render_setting_penalty);

        ImGui.SeparatorText("Feedback");
        setting_row("audio",        render_setting_audio);
        setting_row("parry_effect", render_setting_parry_effect);
        setting_row("impact_shake", render_setting_impact_shake);

        ImGui.SeparatorText("Camera");
        setting_row("battle_camera_lock_mode", render_setting_battle_camera_lock_mode);
        setting_row("magic_camera_lock",       render_setting_magic_camera_lock);

#if DEBUG
        ImGui.SeparatorText("Diagnostics");
        setting_row("logging",       render_setting_logging);
        setting_row("debug_overlay", render_setting_debug_overlay);
        setting_row("camera_probe",  render_setting_camera_probe);
#endif
    }

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

    private void render_setting_overdrive_boost()
    {
        if (ImGui.Checkbox("##fhparry.ctb", ref _optionOverdriveBoost))
        {
            persist_settings();
        }
    }

    private void render_setting_penalty()
    {
        if (ImGui.Checkbox("##fhparry.penalty", ref _optionWhiffLockout))
        {
            persist_settings();
            if (!_optionWhiffLockout && _runtime.InputState == ParryInputState.WhiffLockout)
            {
                // Disabling mid-lockout immediately releases the player.
                _runtime.InputState = ParryInputState.Ready;
                _runtime.WhiffLockoutRemainingSeconds = 0f;
                _runtime.WhiffLockoutTotalSeconds = 0f;
                log_debug("Whiff lockout disabled mid-recovery; returning to Ready.");
            }
        }
    }

    private void render_setting_battle_camera_lock_mode()
    {
        string[] labels = { "Off", "Enemy Turns Only", "All Turns" };
        string[] tooltips =
        {
            "Vanilla FFX camera behavior. No lock applied.",
            "Camera stays put while an enemy is acting (parry-friendly). Your party's turns still pan/zoom as in vanilla.",
            "Camera stays put for every character's turn — yours and theirs. Most static; best for hardcore parry timing.",
        };
        BattleCameraLockMode[] values = { BattleCameraLockMode.Off, BattleCameraLockMode.EnemyTurnsOnly, BattleCameraLockMode.AllTurns };

        int currentIndex = Array.IndexOf(values, _optionBattleCameraLockMode);
        if (currentIndex < 0) currentIndex = 1;  // EnemyTurnsOnly fallback

        if (ImGui.BeginCombo("##fhparry.battle_camera_lock_mode", labels[currentIndex]))
        {
            for (int i = 0; i < labels.Length; i++)
            {
                bool selected = i == currentIndex;
                if (ImGui.Selectable(labels[i], selected))
                {
                    _optionBattleCameraLockMode = values[i];
                    persist_settings();
                    _enemyCameraLockSuppressCount = 0;
                    _enemyMagicCameraLockSuppressCount = 0;
                    _battleSpecialCameraLockSuppressCount = 0;
                    log_debug($"Battle camera lock mode = {_optionBattleCameraLockMode}.");
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(tooltips[i]);
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.TextDisabled("Cinematic cameras (boss / summon / overdrive) are never blocked.");
    }

    private void render_setting_magic_camera_lock()
    {
        if (ImGui.Checkbox("##fhparry.magic_camera_lock", ref _optionMagicCameraLock))
        {
            persist_settings();
            _enemyMagicCameraLockSuppressCount = 0;
            log_debug($"Magic camera lock = {_optionMagicCameraLock}.");
        }
    }

    private void render_setting_parry_effect()
    {
        if (ImGui.Checkbox("##fhparry.parry_effect", ref _optionParryEffect))
        {
            persist_settings();
            string state = _optionParryEffect ? "enabled" : "disabled";
            log_debug($"Parry-success visual effect {state}.");
        }
    }

    private void render_setting_dodge_motion_cancel()
    {
        if (ImGui.Checkbox("##fhparry.dodge_motion_cancel", ref _optionDodgeMotionCancel))
        {
            persist_settings();
            log_debug($"Dodge motion cancel {(_optionDodgeMotionCancel ? "enabled" : "disabled")}.");
        }
    }

    private void render_setting_impact_shake()
    {
        if (ImGui.Checkbox("##fhparry.impact_shake", ref _optionImpactShake))
        {
            persist_settings();
            log_debug($"Impact screen shake {(_optionImpactShake ? "enabled" : "disabled")}.");
        }
    }

    private void render_setting_streak_counter()
    {
        if (ImGui.Checkbox("##fhparry.streak_counter", ref _optionStreakCounter))
        {
            persist_settings();
            string state = _optionStreakCounter ? "enabled (queues counter)" : "disabled (log-only)";
            log_debug($"Streak counter attack {state}.");
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
        int idx = ParryDifficultyModel.GetComboIndex(_optionDifficulty);
        if (ImGui.Combo("##fhparry.difficulty", ref idx, ParryDifficultyModel.GetComboItems()))
        {
            _optionDifficulty = ParryDifficultyModel.DifficultyFromComboIndex(idx);
            persist_settings();
            log_debug($"Difficulty changed to {ParryDifficultyModel.FormatName(_optionDifficulty)}.");
        }
    }

    private void render_setting_dodge_window()
    {
        if (ImGui.SliderFloat("##fhparry.dodge_window", ref _dodgeWindowMs, DodgeWindowMsMin, DodgeWindowMsMax, "%.0f ms"))
        {
            _dodgeWindowMs = Math.Clamp(_dodgeWindowMs, DodgeWindowMsMin, DodgeWindowMsMax);
            persist_settings();
            log_debug($"Dodge window set to {_dodgeWindowMs:F0} ms.");
        }
    }

    private void render_setting_dodge_whiffout()
    {
        if (ImGui.SliderFloat("##fhparry.dodge_whiffout", ref _dodgeWhiffoutMs, DodgeWhiffoutMsMin, DodgeWhiffoutMsMax, "%.0f ms"))
        {
            _dodgeWhiffoutMs = Math.Clamp(_dodgeWhiffoutMs, DodgeWhiffoutMsMin, DodgeWhiffoutMsMax);
            persist_settings();
            log_debug($"Dodge whiffout set to {_dodgeWhiffoutMs:F0} ms.");
        }
    }

    private void render_setting_camera_probe()
    {
        if (ImGui.Checkbox("##fhparry.camera_probe", ref _optionCameraProbe))
        {
            persist_settings();
            log_debug($"Camera probe {(_optionCameraProbe ? "enabled" : "disabled")}.");
        }
    }

}
