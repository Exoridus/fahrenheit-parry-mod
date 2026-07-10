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

        ImGui.SeparatorText("Reward");
        setting_row("streak_counter", render_setting_streak_counter);

        ImGui.SeparatorText("Feedback");
        setting_row("audio",        render_setting_audio);
        setting_row("parry_effect", render_setting_parry_effect);
        setting_row("impact_shake", render_setting_impact_shake);

        ImGui.SeparatorText("Camera");
        setting_row("battle_camera_lock_mode", render_setting_battle_camera_lock_mode);
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

    private void render_setting_parry_effect()
    {
        if (ImGui.Checkbox("##fhparry.parry_effect", ref _optionParryEffect))
        {
            persist_settings();
            string state = _optionParryEffect ? "enabled" : "disabled";
            log_debug($"Parry-success visual effect {state}.");
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

    private void render_setting_difficulty()
    {
        int idx = ParryDifficultyModel.GetComboIndex(_optionDifficulty);
        if (ImGui.Combo("##fhparry.difficulty", ref idx, ParryDifficultyModel.GetComboItems()))
        {
            _optionDifficulty = ParryDifficultyModel.DifficultyFromComboIndex(idx);
            persist_settings();
            log_debug($"Difficulty changed to {ParryDifficultyModel.FormatName(_optionDifficulty)}.");
        }

        render_difficulty_timing_info();
    }

    // The selected preset's timing, in battle frames (30 Hz ticks) with the nominal wall-clock ms.
    // Commitment is the parry window plus the whiff-recovery lockout — the full cost of a press.
    private void render_difficulty_timing_info()
    {
        int parry  = ParryDifficultyModel.GetParryWindowTicks(_optionDifficulty);
        int dodge  = ParryDifficultyModel.GetDodgeWindowTicks(_optionDifficulty);
        int commit = ParryDifficultyModel.GetTotalCommitmentTicks(_optionDifficulty);
        ImGui.TextDisabled(
            $"Parry {parry}f ({ParryDifficultyModel.TicksToMs(parry):F0} ms)   ·   "
            + $"Dodge {dodge}f ({ParryDifficultyModel.TicksToMs(dodge):F0} ms)   ·   "
            + $"Commitment {commit}f ({ParryDifficultyModel.TicksToMs(commit):F0} ms)");
    }

}
