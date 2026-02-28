namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule {
    private void render_setting_timing_mode() {
        ImGui.BeginGroup();
        string legacyLabel = FhApi.Localization.localize("fhparry.timing_mode.legacy");
        string resolveLabel = FhApi.Localization.localize("fhparry.timing_mode.resolve");
        bool legacySelected = _optionTimingMode == ParryTimingMode.FixedWindow;
        bool resolveSelected = _optionTimingMode == ParryTimingMode.ApplyDamageClamp;
        if (ImGui.RadioButton($"{legacyLabel}##fhparry.timing_mode.legacy", legacySelected)) {
            _optionTimingMode = ParryTimingMode.FixedWindow;
        }

        if (ImGui.RadioButton($"{resolveLabel}##fhparry.timing_mode.resolve", resolveSelected)) {
            _optionTimingMode = ParryTimingMode.ApplyDamageClamp;
        }

        ImGui.EndGroup();
    }

    private void render_setting_enabled() {
        if (ImGui.Checkbox("##fhparry.enabled", ref _optionEnabled)) {
            log_debug($"Master toggle changed: {_optionEnabled}.");
            if (!_optionEnabled) {
                reset_runtime_state("disabled_setting", clearFeedbackFlashes: true, clearDamageFlags: true);
                log_debug("Disabled while active; runtime state reset.");
            }
        }
    }

    private void render_setting_indicator() {
        ImGui.Checkbox("##fhparry.indicator", ref _optionIndicator);
    }

    private void render_setting_audio() {
        ImGui.Checkbox("##fhparry.audio", ref _optionSound);
    }

    private void render_setting_overdrive_boost() {
        ImGui.Checkbox("##fhparry.ctb", ref _optionOverdriveBoost);
    }

    private void render_setting_negate() {
        ImGui.Checkbox("##fhparry.negate", ref _optionNegateDamage);
    }

    private void render_setting_logging() {
        if (ImGui.Checkbox("##fhparry.logging", ref _optionLogging)) {
            string state = _optionLogging ? "enabled" : "disabled";
            _logger.Info($"[Parry] Debug logging {state} via settings.");
        }
    }

    private void render_setting_debug_overlay() {
        if (ImGui.Checkbox("##fhparry.debug_overlay", ref _optionDebugOverlay)) {
            string state = _optionDebugOverlay ? "enabled" : "disabled";
            log_debug($"Debug overlay {state}.");
        }
    }

    private void render_setting_window() {
        bool disabled = _optionTimingMode != ParryTimingMode.FixedWindow;
        if (disabled) ImGui.BeginDisabled();
        ImGui.SliderFloat("##fhparry.window_seconds", ref _optionWindowSeconds, WindowMinSeconds, WindowMaxSeconds, "%.1f s");
        if (disabled) ImGui.EndDisabled();
    }

    private void render_setting_resolve_window() {
        bool disabled = _optionTimingMode != ParryTimingMode.ApplyDamageClamp;
        if (disabled) ImGui.BeginDisabled();
        ImGui.SliderFloat("##fhparry.resolve_window", ref _optionResolveWindowSeconds, ResolveWindowMinSeconds, ResolveWindowMaxSeconds, "%.1f s");
        if (disabled) ImGui.EndDisabled();
    }

    private void render_setting_lead_physical() {
        ImGui.SliderFloat("##fhparry.lead_physical", ref _optionLeadPhysicalSeconds, LeadPhysicalMinSeconds, LeadPhysicalMaxSeconds, "%.2f s");
    }

    private void render_setting_lead_magic() {
        ImGui.SliderFloat("##fhparry.lead_magic", ref _optionLeadMagicSeconds, LeadMagicMinSeconds, LeadMagicMaxSeconds, "%.2f s");
    }

    private void render_setting_future() {
        ImGui.BeginDisabled(true);
        ImGui.TextWrapped("Auto-counter customization coming soon.");
        ImGui.EndDisabled();
    }
}
