namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    // Localized display name / hover description for a setting id, from the fhparry.<id>.name /
    // .desc keys in lang/*.json. Fahrenheit's own settings panel used to draw these for us; since
    // alpha11 removed FhSettingCustomRenderer we draw the chrome ourselves.
    private static string setting_label(string id) => FhApi.Localization.localize($"fhparry.{id}.name");

    private static void setting_tooltip(string id)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(FhApi.Localization.localize($"fhparry.{id}.desc"));
        }
    }

    /// <summary>
    ///     The Settings tab. Replaces the FhSettingsCategory registration that alpha11 removed;
    ///     persistence is unaffected — the mod has always written its own fhparry.config.json.
    ///     Layout: the master toggle and the two combos run full width; the feedback toggles pack
    ///     into a two-column grid so the checkbox and its label sit side by side, not stacked.
    /// </summary>
    private void render_settings_tab()
    {
        render_setting_enabled();

        ImGui.Spacing();
        labeled_combo("difficulty", render_setting_difficulty);

        ImGui.SeparatorText("Feedback");
        if (ImGui.BeginTable("##fhparry.toggles", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); render_setting_audio();
            ImGui.TableNextColumn(); render_setting_parry_effect();
            ImGui.TableNextColumn(); render_setting_impact_shake();
            ImGui.TableNextColumn(); render_setting_streak_counter();
            ImGui.EndTable();
        }

        ImGui.SeparatorText("Camera");
        labeled_combo("battle_camera_lock_mode", render_setting_battle_camera_lock_mode);
    }

    // A combo (or combo-like renderer) preceded by its label on the same line, the widget filling
    // the remaining width. The renderer draws a hidden-label combo and may trail extra lines.
    private static void labeled_combo(string id, Action widget)
    {
        ImGui.TextUnformatted(setting_label(id));
        setting_tooltip(id);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        widget();
    }

    private void render_setting_enabled()
    {
        if (ImGui.Checkbox(setting_label("enabled") + "##fhparry.enabled", ref _optionEnabled))
        {
            persist_settings();
            log_debug($"Master toggle changed: {_optionEnabled}.");
            if (!_optionEnabled)
            {
                reset_runtime_state("disabled_setting", clearFeedbackFlashes: true, clearDamageFlags: true);
                log_debug("Disabled while active; runtime state reset.");
            }
        }
        setting_tooltip("enabled");
    }

    private void render_setting_audio()
    {
        if (ImGui.Checkbox(setting_label("audio") + "##fhparry.audio", ref _optionSound))
        {
            persist_settings();
            if (!_optionSound)
            {
                stop_audio_playback();
            }
        }
        setting_tooltip("audio");
    }

    private void render_setting_parry_effect()
    {
        if (ImGui.Checkbox(setting_label("parry_effect") + "##fhparry.parry_effect", ref _optionParryEffect))
        {
            persist_settings();
            string state = _optionParryEffect ? "enabled" : "disabled";
            log_debug($"Parry-success visual effect {state}.");
        }
        setting_tooltip("parry_effect");
    }

    private void render_setting_impact_shake()
    {
        if (ImGui.Checkbox(setting_label("impact_shake") + "##fhparry.impact_shake", ref _optionImpactShake))
        {
            persist_settings();
            log_debug($"Impact screen shake {(_optionImpactShake ? "enabled" : "disabled")}.");
        }
        setting_tooltip("impact_shake");
    }

    private void render_setting_streak_counter()
    {
        if (ImGui.Checkbox(setting_label("streak_counter") + "##fhparry.streak_counter", ref _optionStreakCounter))
        {
            persist_settings();
            string state = _optionStreakCounter ? "enabled (queues counter)" : "disabled (log-only)";
            log_debug($"Streak counter attack {state}.");
        }
        setting_tooltip("streak_counter");
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
}
