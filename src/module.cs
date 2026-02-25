// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

[FhLoad(FhGameId.FFX)]
public unsafe sealed class ParryModule : FhModule {
    private const int   PartyActorCapacity     = 10;
    private const int   EnemyActorCapacity     = 10;
    private const uint  PlayerTargetMask       = (1u << PartyActorCapacity) - 1u;
    private const int   SoundResetFrames       = 6;
    private const int   IndicatorFlashFrames   = 45;
    private const float BattleFrameRate           = 30f;
    private const float FrameDurationSeconds      = 1f / BattleFrameRate;
    private const float WindowMinSeconds          = 0.5f;
    private const float WindowMaxSeconds          = 2f;
    private const float WindowStepSeconds         = 0.1f;
    private const float LeadPhysicalMinSeconds    = 0f;
    private const float LeadPhysicalMaxSeconds    = 0.5f;
    private const float LeadMagicMinSeconds       = 0f;
    private const float LeadMagicMaxSeconds       = 1.0f;
    private const float OverdriveBoostPercent        = 0.05f;
    private const float OverlayAnimDurationSeconds   = 0.4f;
    private const float OverlayScaleDurationSeconds  = 0.18f;
    private const float ResolveWindowMinSeconds   = 0.2f;
    private const float ResolveWindowMaxSeconds   = 2f;

    private enum ParryTimingMode {
        FixedWindow,
        ApplyDamageClamp
    }

    private enum ParryOverlayState {
        Hidden,
        Parry,
        Success,
        Failure
    }

    private bool _optionEnabled      = true;
    private bool _optionIndicator    = true;
    private bool _optionSound        = true;
    private bool _optionLogging      = true;
    private bool _optionOverdriveBoost = true;
    private bool _optionNegateDamage = true;
    private float _optionWindowSeconds       = 1.0f;
    private float _optionLeadPhysicalSeconds = 0.10f;
    private float _optionLeadMagicSeconds    = 0.30f;
    private ParryTimingMode _optionTimingMode      = ParryTimingMode.ApplyDamageClamp;
    private float           _optionResolveWindowSeconds = 0.8f;

    private FhTexture? _bannerTextureParry;
    private FhTexture? _bannerTextureSuccess;
    private FhTexture? _bannerTextureFail;
    private string?    _bannerPathParry;
    private string?    _bannerPathSuccess;
    private string?    _bannerPathFail;
    private bool       _bannerParryWarned;
    private bool       _bannerSuccessWarned;
    private bool       _bannerFailWarned;

    private bool _parryWindowActive;
    private byte _currentAttackerId;
    private uint _currentPartyTargetMask;
    private int  _parryWindowFrames;
    private bool _awaitingTurnEnd;
    private bool _leadPending;
    private int  _leadFramesRemaining;
    private byte _leadAttackerId;
    private int  _parryWindowElapsedFrames;
    private int  _pendingLeadFramesApplied;
    private int  _parryWindowDebounceFrames;
    private bool _parryInputDebounced;
    private bool _parryWindowSucceeded;
    private bool _successIndicatorActive;

    private Chr* _pendingSoundChr;
    private int  _pendingSoundFrames;
    private uint _pendingNegateMask;
    private int  _pendingNegateTimeoutFrames;

    private int   _successFlashFrames;
    private int   _failureFlashFrames;
    private float _overlayAnimProgress;
    private float _overlayScaleProgress;
    private ParryOverlayState _overlayState      = ParryOverlayState.Hidden;
    private ParryOverlayState _lastOverlayState  = ParryOverlayState.Hidden;
    private readonly bool[]            _damageEventActive = new bool[PartyActorCapacity];
    private ParryTimingTimeline?       _activeTiming;
    private string?                    _timingLogPath;
    private readonly JsonSerializerOptions _timingJsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false,
        Converters           = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ParryModule() {
        settings = new FhSettingsCategory("fhparry", [
            new FhSettingCustomRenderer("enabled", render_setting_enabled),
            new FhSettingCustomRenderer("timing_mode", render_setting_timing_mode),
            new FhSettingCustomRenderer("window_seconds", render_setting_window),
            new FhSettingCustomRenderer("resolve_window", render_setting_resolve_window),
            new FhSettingCustomRenderer("indicator", render_setting_indicator),
            new FhSettingCustomRenderer("audio", render_setting_audio),
            new FhSettingCustomRenderer("ctb", render_setting_overdrive_boost),
            new FhSettingCustomRenderer("logging", render_setting_logging),
            new FhSettingCustomRenderer("negate", render_setting_negate),
            new FhSettingCustomRenderer("lead_physical", render_setting_lead_physical),
            new FhSettingCustomRenderer("lead_magic", render_setting_lead_magic),
            new FhSettingCustomRenderer("future", render_setting_future)
        ]);
    }

    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        _bannerPathParry   = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "parry.png");
        _bannerPathSuccess = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "success.png");
        _bannerPathFail    = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "toobad.png");
        _timingLogPath     = Path.Combine(mod_context.Paths.ModDir.FullName, "parry_timings.jsonl");

        _logger.Info("ParryPrototype ready. Adjust options via Mod Config (F7).");
        return true;
    }

    public override void pre_update() {
        if (!_optionEnabled) {
            finalize_timing_capture("disabled");
            _parryWindowActive = false;
            _awaitingTurnEnd   = false;
            _leadPending       = false;
            _parryWindowElapsedFrames = 0;
            _pendingNegateMask       = 0;
            _pendingNegateTimeoutFrames = 0;
            _parryWindowDebounceFrames = 0;
            _parryInputDebounced       = false;
            _parryWindowSucceeded      = false;
            _successIndicatorActive    = false;
            update_overlay_animation_state();
            return;
        }

        if (_parryWindowDebounceFrames > 0)
            _parryWindowDebounceFrames--;

        bool hasEnemyCue = monitor_attack_cues();
        process_lead_pending();
        monitor_damage_resolves();
        process_pending_negation();
        update_sound_flag();

        if (_parryWindowActive) {
            _parryWindowElapsedFrames++;
            _parryWindowFrames--;
            if (_parryWindowFrames <= 0) {
                trigger_failure_feedback();
                end_parry_window("timeout");
            }
            else if (!_parryInputDebounced && FhApi.Input.r1.just_pressed) {
                _parryInputDebounced = true;
                on_parry_success();
            }
        }
        else if (!_leadPending && _awaitingTurnEnd && !hasEnemyCue) {
            _awaitingTurnEnd = false;
            _parryInputDebounced = false;
            if (_successIndicatorActive) {
                _successIndicatorActive = false;
                _successFlashFrames     = Math.Max(_successFlashFrames, IndicatorFlashFrames);
            }
            _parryWindowSucceeded = false;
        }

        if (_successFlashFrames > 0) _successFlashFrames--;
        if (_failureFlashFrames > 0) _failureFlashFrames--;

        update_overlay_animation_state();
    }

    public override void render_imgui() {
        render_parry_window_overlay();
    }

    private void render_parry_window_overlay() {
        if (!_optionIndicator) return;

        float visibility = compute_overlay_animation_progress();
        bool showWindow = _overlayState != ParryOverlayState.Hidden;
        if (!showWindow && visibility <= 0.01f) return;

        ensure_banner_textures();

        ParryOverlayState displayState = showWindow ? _overlayState : _lastOverlayState;
        if (displayState == ParryOverlayState.Hidden) return;

        FhTexture? texture = displayState switch {
            ParryOverlayState.Parry   => _bannerTextureParry,
            ParryOverlayState.Success => _bannerTextureSuccess,
            _                         => _bannerTextureFail
        };

        Vector2 imageSize = texture != null
            ? new Vector2((float)texture.Metadata.width, (float)texture.Metadata.height)
            : new Vector2(640f, 260f);
        Vector2 windowSize = texture != null ? imageSize : imageSize + new Vector2(120f, 80f);

        float scale = compute_eased_scale(_overlayScaleProgress);

        Vector2 displaySize = ImGui.GetIO().DisplaySize;
        Vector2 anchor      = new(displaySize.X * 0.5f, displaySize.Y * 0.32f);

        ImGui.SetNextWindowPos(anchor, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, texture != null ? new Vector4(0f, 0f, 0f, 0f) : new Vector4(0f, 0f, 0f, 0.65f));
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize;
        if (ImGui.Begin("ParryOverlay", flags)) {
            if (texture != null) {
                Vector2 drawSize = imageSize * scale;
                Vector2 cursor = (windowSize - drawSize) * 0.5f;
                ImGui.SetCursorPos(cursor);
                ImGui.Image(texture.TextureRef, drawSize);
            }
            else {
                Vector4 color = displayState switch {
                    ParryOverlayState.Parry   => new Vector4(1f, 1f, 0.2f, 1f),
                    ParryOverlayState.Success => new Vector4(0.2f, 0.95f, 0.2f, 1f),
                    _                         => new Vector4(0.95f, 0.2f, 0.2f, 1f)
                };

                ImGui.SetCursorPos(new Vector2(40f, windowSize.Y * 0.35f));
                ImGuiNativeExtra.igSetWindowFontScale(2.4f * scale);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                string label = displayState switch {
                    ParryOverlayState.Parry   => "PARRY",
                    ParryOverlayState.Success => "SUCCESS",
                    _                         => "MISSED"
                };
                ImGui.Text(label);
                ImGui.PopStyleColor();
                ImGuiNativeExtra.igSetWindowFontScale(1f);
            }
        }
        ImGui.End();

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private void render_hook_indicator() { }

    private void render_setting_timing_mode() {
        ImGui.BeginGroup();
        string legacyLabel  = FhApi.Localization.localize("fhparry.timing_mode.legacy");
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
                _parryWindowActive = false;
                _awaitingTurnEnd   = false;
                log_debug("Disabled while window active; clearing state.");
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

    private void ensure_banner_textures() {
        if (_bannerTextureParry == null)
            _bannerTextureParry = try_load_banner_texture(_bannerPathParry, "parry.png", ref _bannerParryWarned);
        if (_bannerTextureSuccess == null)
            _bannerTextureSuccess = try_load_banner_texture(_bannerPathSuccess, "success.png", ref _bannerSuccessWarned);
        if (_bannerTextureFail == null)
            _bannerTextureFail = try_load_banner_texture(_bannerPathFail, "toobad.png", ref _bannerFailWarned);
    }

    private FhTexture? try_load_banner_texture(string? path, string label, ref bool warned) {
        if (string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path)) {
            if (!warned) {
                _logger.Warning($"Banner image '{label}' was not found at {path}.");
                warned = true;
            }
            return null;
        }

        if (!FhApi.Resources.load_png_from_disk(path, out FhTexture? texture)) {
            if (!warned) {
                _logger.Warning($"Failed to load banner image '{label}' from {path}.");
                warned = true;
            }
            return null;
        }

        warned = false;
        return texture;
    }

    private void monitor_damage_resolves() {
        Chr* party = FhFfx.Globals.Battle.player_characters;
        if (party == null) {
            Array.Clear(_damageEventActive);
            return;
        }

        for (int i = 0; i < PartyActorCapacity; i++) {
            Chr* chr = party + i;
            bool hasDamage = chr != null && (chr->damage_hp != 0 || chr->damage_mp != 0);
            if (hasDamage && !_damageEventActive[i]) {
                _damageEventActive[i] = true;
                if (is_resolve_mode())
                    on_damage_resolve_detected(i);
            }
            else if (!hasDamage && _damageEventActive[i]) {
                _damageEventActive[i] = false;
            }
        }
    }

    private void on_damage_resolve_detected(int slotIndex) {
        record_timing_hit(slotIndex);
        if (!_parryWindowActive || !is_resolve_mode()) return;

        uint bit = 1u << slotIndex;
        uint mask = _currentPartyTargetMask == 0 ? PlayerTargetMask : _currentPartyTargetMask;
        if (mask != 0 && (mask & bit) == 0) return;

        log_debug($"Detected incoming damage for party slot {slotIndex}; closing parry window.");
        trigger_failure_feedback();
        end_parry_window("damage_resolve");
    }

    private bool monitor_attack_cues() {
        bool hasEnemyCue = try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker);

        if (hasEnemyCue) {
            uint partyMask = extract_party_target_mask(cue);
            if (partyMask == 0) {
                log_debug($"Enemy #{cue.attacker_id} action ignored (no party targets).");
                return true;
            }

            if (!_parryWindowActive && !_leadPending && !_awaitingTurnEnd && _parryWindowDebounceFrames <= 0) {
                start_lead_or_window(cue, cueIndex, attacker, partyMask);
            }
            return true;
        }

        if (_parryWindowActive) {
            log_debug("Enemy cue cleared without parry input; closing window.");
            end_parry_window("cue_cleared");
        }

        if (_leadPending) {
            log_debug("Lead-in cancelled because cue disappeared.");
            _leadPending = false;
            _pendingLeadFramesApplied = 0;
        }

        if (_awaitingTurnEnd) {
            _awaitingTurnEnd = false;
            if (_successIndicatorActive) {
                _successIndicatorActive = false;
                _successFlashFrames     = Math.Max(_successFlashFrames, IndicatorFlashFrames);
            }
            _parryWindowSucceeded = false;
            log_debug("Enemy action resolved; parry ready.");
        }

        return false;
    }

    private void start_lead_or_window(AttackCue cue, byte cueIndex, Chr* attacker, uint partyMask) {
        bool isMagic = is_magic_like_attack(attacker);
        int  leadFrames = compute_lead_frames(isMagic);

        if (leadFrames <= 0) {
            _pendingLeadFramesApplied = 0;
            begin_parry_window(cue, cueIndex, partyMask, 0);
            return;
        }

        _leadPending             = true;
        _leadFramesRemaining     = leadFrames;
        _pendingLeadFramesApplied = leadFrames;
        _leadAttackerId          = cue.attacker_id;
        _awaitingTurnEnd         = true;
        _currentPartyTargetMask  = partyMask;
        log_debug($"Lead delay for attacker #{_leadAttackerId}: {leadFrames} frames (magic={isMagic}, targets=0x{partyMask:X}).");
    }

    private void process_lead_pending() {
        if (!_leadPending) return;

        if (!try_get_enemy_attack_cue(_leadAttackerId, out AttackCue cue, out byte cueIndex, out Chr* _)) {
            _leadPending             = false;
            _awaitingTurnEnd         = false;
            _pendingLeadFramesApplied = 0;
            log_debug("Lead-in cancelled because attacker left the cue list.");
            return;
        }

        _leadFramesRemaining--;
        if (_leadFramesRemaining > 0) return;

        uint partyMask = extract_party_target_mask(cue);
        if (partyMask == 0) {
            _leadPending             = false;
            _awaitingTurnEnd         = false;
            _pendingLeadFramesApplied = 0;
            log_debug("Lead-in cancelled due to no remaining party targets.");
            return;
        }

        _leadPending = false;
        begin_parry_window(cue, cueIndex, partyMask, _pendingLeadFramesApplied);
        _pendingLeadFramesApplied = 0;
    }

    private void begin_parry_window(AttackCue cue, byte cueIndex, uint partyMask, int leadFramesUsed) {
        _leadPending         = false;
        _parryWindowActive   = true;
        _awaitingTurnEnd     = true;
        _currentAttackerId   = cue.attacker_id;
        _currentPartyTargetMask = partyMask;
        _parryWindowFrames   = compute_initial_window_frames();
        _parryWindowElapsedFrames = 0;
        _failureFlashFrames  = 0;
        _parryWindowDebounceFrames = 0;
        _parryInputDebounced       = false;
        _parryWindowSucceeded      = false;
        _successIndicatorActive    = false;
        start_timing_session(cue, cueIndex, partyMask, leadFramesUsed);
        log_debug($"Enemy #{_currentAttackerId} command detected (cue {cueIndex}) - parry window open for {_parryWindowFrames} frames targeting mask 0x{partyMask:X}.");
    }

    private bool try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker) {
        return try_get_enemy_attack_cue_internal(null, out cue, out cueIndex, out attacker);
    }

    private bool try_get_enemy_attack_cue(byte attackerFilter, out AttackCue cue, out byte cueIndex, out Chr* attacker) {
        return try_get_enemy_attack_cue_internal(attackerFilter, out cue, out cueIndex, out attacker);
    }

    private bool try_get_enemy_attack_cue_internal(byte? attackerFilter, out AttackCue cue, out byte cueIndex, out Chr* attacker) {
        cueIndex = 0;
        cue      = default;
        attacker = null;

        Btl* battle = FhFfx.Globals.Battle.btl;
        if (battle == null) return false;

        byte totalCues = battle->attack_cues_size;
        for (byte i = 0; i < totalCues; i++) {
            AttackCue candidate = battle->attack_cues[i];
            Chr* candidateChr = try_get_chr(candidate.attacker_id);
            if (!should_flag_as_enemy(candidate.attacker_id, candidateChr))
                continue;
            if (attackerFilter.HasValue && candidate.attacker_id != attackerFilter.Value)
                continue;

            cueIndex = i;
            cue      = candidate;
            attacker = candidateChr;
            return true;
        }

        return false;
    }

    private uint extract_party_target_mask(AttackCue cue) {
        uint mask = 0;
        int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
        for (int i = 0; i < commandCount; i++) {
            ref AttackCommandInfo info = ref cue.command_list[i];
            mask |= info.targets & PlayerTargetMask;
        }
        return mask;
    }

    private int compute_initial_window_frames() {
        return is_resolve_mode() ? compute_resolve_window_frames() : compute_window_frames();
    }

    private int compute_negation_timeout_frames() {
        return compute_initial_window_frames();
    }

    private int compute_resolve_window_frames() {
        float clamped = Math.Clamp(_optionResolveWindowSeconds, ResolveWindowMinSeconds, ResolveWindowMaxSeconds);
        return seconds_to_frames(clamped);
    }

    private bool is_resolve_mode() {
        return _optionTimingMode == ParryTimingMode.ApplyDamageClamp;
    }

    private int compute_window_frames() {
        int minFrames = seconds_to_frames(WindowMinSeconds);
        int maxFrames = seconds_to_frames(WindowMaxSeconds);
        int target    = seconds_to_frames(_optionWindowSeconds);
        return Math.Clamp(target, minFrames, maxFrames);
    }

    private int compute_lead_frames(bool isMagic) {
        float minSeconds = isMagic ? LeadMagicMinSeconds : LeadPhysicalMinSeconds;
        float maxSeconds = isMagic ? LeadMagicMaxSeconds : LeadPhysicalMaxSeconds;
        float option     = isMagic ? _optionLeadMagicSeconds : _optionLeadPhysicalSeconds;
        float clamped    = Math.Clamp(option, minSeconds, maxSeconds);
        return seconds_to_frames(clamped);
    }

    private static int seconds_to_frames(float seconds) {
        int frames = (int)MathF.Round(seconds * BattleFrameRate);
        return Math.Max(frames, 0);
    }

    private bool is_magic_like_attack(Chr* attacker) {
        if (attacker == null) return false;
        byte commandType = attacker->stat_command_type;
        if (commandType >= 2) return true;
        if (commandType == 1) return true;
        return false;
    }

    private Chr* try_get_chr(byte slotIndex) {
        Chr* party   = FhFfx.Globals.Battle.player_characters;
        Chr* enemies = FhFfx.Globals.Battle.monster_characters;

        if (party != null && slotIndex < PartyActorCapacity)
            return party + slotIndex;

        int enemyIdx = slotIndex - PartyActorCapacity;
        if (enemies != null && enemyIdx >= 0 && enemyIdx < EnemyActorCapacity)
            return enemies + enemyIdx;

        return null;
    }

    private bool should_flag_as_enemy(byte slotIndex, Chr* chr) {
        if (chr != null) {
            if (chr->stat_group != 0) return true;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) return slotIndex >= PartyActorCapacity;
        }

        return slotIndex >= PartyActorCapacity;
    }

    private void end_parry_window(string reason) {
        if (_parryWindowActive)
            log_debug($"Parry window closed for attacker #{_currentAttackerId}.");
        finalize_timing_capture(reason);
        _parryWindowActive = false;
        _parryWindowFrames = 0;
        _parryWindowElapsedFrames = 0;
        _parryWindowDebounceFrames = 0;
        _parryInputDebounced       = false;
        _parryWindowSucceeded      = false;
        _successIndicatorActive    = false;
    }

    private void on_parry_success() {
        int framesRemaining = Math.Max(_parryWindowFrames, 0);
        _parryWindowActive = false;
        _parryWindowFrames = 0;
        _awaitingTurnEnd   = true;
        _parryWindowDebounceFrames = Math.Max(_parryWindowDebounceFrames, Math.Max(framesRemaining, 1));

        _parryWindowSucceeded   = true;
        _successIndicatorActive = true;
        _successFlashFrames     = Math.Max(_successFlashFrames, IndicatorFlashFrames);
        _failureFlashFrames = 0;

        log_debug($"Parry input detected against attacker #{_currentAttackerId}.");
        finalize_timing_capture("parry_success", true);
        mark_pending_negation();
        apply_overdrive_boost(_currentPartyTargetMask);
        play_feedback_sound();
    }

    private void trigger_failure_feedback() {
        if (!_parryWindowActive || _parryWindowSucceeded) return;
        log_debug($"Parry failed against attacker #{_currentAttackerId}.");
        if (_optionIndicator)
            _failureFlashFrames = IndicatorFlashFrames;
        _successFlashFrames = 0;
    }

    private void mark_pending_negation() {
        if (!_optionNegateDamage) return;

        uint mask = _currentPartyTargetMask;
        if (mask == 0) mask = PlayerTargetMask;

        _pendingNegateMask          = mask;
        _pendingNegateTimeoutFrames = compute_negation_timeout_frames();
        log_debug($"Queued damage negation for targets mask 0x{mask:X}.");
    }

    private void process_pending_negation() {
        if (_pendingNegateMask == 0 || !_optionNegateDamage) return;

        Chr* party = FhFfx.Globals.Battle.player_characters;
        if (party == null) {
            _pendingNegateMask = 0;
            return;
        }

        for (int i = 0; i < PartyActorCapacity; i++) {
            uint bit = 1u << i;
            if ((_pendingNegateMask & bit) == 0) continue;

            Chr* chr = party + i;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) {
                _pendingNegateMask &= ~bit;
                continue;
            }

            if (chr->damage_hp == 0 && chr->damage_mp == 0)
                continue;

            chr->damage_hp       = 0;
            chr->damage_mp       = 0;
            chr->damage_ctb      = 0;
            chr->stat_avoid_flag = true;
            _pendingNegateMask  &= ~bit;
            log_debug($"Negated pending damage for party slot {i}.");
        }

        if (_pendingNegateMask == 0) {
            _pendingNegateTimeoutFrames = 0;
            return;
        }

        if (_pendingNegateTimeoutFrames > 0) {
            _pendingNegateTimeoutFrames--;
            if (_pendingNegateTimeoutFrames == 0)
                _pendingNegateMask = 0;
        }
    }

    private void apply_overdrive_boost(uint mask) {
        if (!_optionOverdriveBoost) return;

        Chr* party = FhFfx.Globals.Battle.player_characters;
        if (party == null) return;

        uint effectiveMask = mask == 0 ? PlayerTargetMask : mask;

        for (int i = 0; i < PartyActorCapacity; i++) {
            uint bit = 1u << i;
            if ((effectiveMask & bit) == 0) continue;

            Chr* chr = party + i;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) continue;

            byte maxCharge = chr->ram.limit_charge_max;
            if (maxCharge == 0) continue;

            int before = chr->ram.limit_charge;
            int delta  = Math.Max(1, (int)MathF.Round(maxCharge * OverdriveBoostPercent));
            int after  = Math.Clamp(before + delta, 0, maxCharge);
            if (after == before) continue;

            chr->ram.limit_charge = (byte)after;
            log_debug($"Increased overdrive for party slot {i} from {before} to {after}.");
        }
    }

    private void play_feedback_sound() {
        if (!_optionSound) return;

        Chr* player = find_first_active_player();
        if (player == null) return;

        player->stat_sound_hit_num = 3;
        _pendingSoundChr    = player;
        _pendingSoundFrames = SoundResetFrames;
        log_debug("Queued confirm-style hit sound for local player.");
    }

    private void update_sound_flag() {
        if (_pendingSoundChr == null || _pendingSoundFrames <= 0) return;

        _pendingSoundFrames--;
        if (_pendingSoundFrames == 0) {
            _pendingSoundChr->stat_sound_hit_num = 0;
            _pendingSoundChr = null;
            log_debug("Reset temporary hit sound flag.");
        }
    }

    private Chr* find_first_active_player() {
        Chr* party = FhFfx.Globals.Battle.player_characters;
        if (party == null) return null;

        for (int i = 0; i < PartyActorCapacity; i++) {
            Chr* chr = party + i;
            if (chr->stat_exist_flag && chr->ram.hp > 0)
                return chr;
        }

        return null;
    }

    private void update_overlay_animation_state() {
        ParryOverlayState nextState = _parryWindowActive
            ? ParryOverlayState.Parry
            : (_successIndicatorActive || _successFlashFrames > 0)
                ? ParryOverlayState.Success
                : _failureFlashFrames > 0
                    ? ParryOverlayState.Failure
                    : ParryOverlayState.Hidden;

        if (nextState != _overlayState) {
            _overlayState = nextState;
            if (_overlayState != ParryOverlayState.Hidden) {
                _lastOverlayState   = _overlayState;
                _overlayScaleProgress = 0f;
            }
        }

        float targetVisibility = _overlayState == ParryOverlayState.Hidden ? 0f : 1f;
        float visibilityDelta  = FrameDurationSeconds / OverlayAnimDurationSeconds;
        if (targetVisibility > _overlayAnimProgress)
            _overlayAnimProgress = MathF.Min(1f, _overlayAnimProgress + visibilityDelta);
        else
            _overlayAnimProgress = MathF.Max(0f, _overlayAnimProgress - visibilityDelta);

        if (_overlayState != ParryOverlayState.Hidden) {
            float scaleDelta = FrameDurationSeconds / OverlayScaleDurationSeconds;
            _overlayScaleProgress = MathF.Min(1f, _overlayScaleProgress + scaleDelta);
        }
    }

    private float compute_overlay_animation_progress() {
        return _overlayAnimProgress;
    }

    private float compute_eased_scale(float progress) {
        float eased = evaluate_cubic_bezier(progress, 1.6f, 0.8f);
        return 0.5f + 0.5f * Math.Clamp(eased, 0f, 1.2f);
    }

    private static float evaluate_cubic_bezier(float t, float p1y, float p2y) {
        float inv = 1f - t;
        return inv * inv * inv * 0f
             + 3f * inv * inv * t * p1y
             + 3f * inv * t * t * p2y
             + t * t * t;
    }

    private void log_debug(string message) {
        if (_optionLogging)
            _logger.Info($"[Parry] {message}");
    }

    private static float frames_to_seconds(int frames) {
        return frames / BattleFrameRate;
    }

    private void record_timing_hit(int slotIndex) {
        if (_activeTiming == null) return;
        _activeTiming.Events.Add(new ParryTimingEvent {
            Type        = "hit",
            Slot        = slotIndex,
            TimeSeconds = frames_to_seconds(_parryWindowElapsedFrames)
        });
    }

    private void start_timing_session(AttackCue cue, byte cueIndex, uint partyMask, int leadFramesUsed) {
        uint[]? commandTargets = null;
        int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
        if (commandCount > 0) {
            commandTargets = new uint[commandCount];
            for (int i = 0; i < commandCount; i++)
                commandTargets[i] = cue.command_list[i].targets;
        }

        _activeTiming = new ParryTimingTimeline {
            TimestampUtc         = DateTime.UtcNow.ToString("O"),
            AttackerId           = cue.attacker_id,
            CueIndex             = cueIndex,
            TargetMask           = partyMask,
            CommandCount         = (byte)commandCount,
            CommandTargets       = commandTargets,
            TimingMode           = _optionTimingMode,
            LeadSeconds          = frames_to_seconds(leadFramesUsed),
            LegacyWindowSeconds  = Math.Clamp(_optionWindowSeconds, WindowMinSeconds, WindowMaxSeconds),
            ResolveWindowSeconds = Math.Clamp(_optionResolveWindowSeconds, ResolveWindowMinSeconds, ResolveWindowMaxSeconds)
        };
    }

    private void finalize_timing_capture(string reason, bool parrySucceeded = false) {
        if (_activeTiming == null) return;

        _activeTiming.EndSeconds    = frames_to_seconds(_parryWindowElapsedFrames);
        _activeTiming.EndReason     = reason;
        _activeTiming.ParrySucceeded = parrySucceeded;

        if (!string.IsNullOrEmpty(_timingLogPath)) {
            try {
                string json = JsonSerializer.Serialize(_activeTiming, _timingJsonOptions);
                File.AppendAllText(_timingLogPath!, json + Environment.NewLine);
            }
            catch (Exception ex) {
                _logger.Warning($"Failed to write parry timing sample: {ex.Message}");
            }
        }

        _activeTiming = null;
    }

    private sealed class ParryTimingTimeline {
        public string                 TimestampUtc         { get; init; } = DateTime.UtcNow.ToString("O");
        public byte                   AttackerId           { get; init; }
        public byte                   CueIndex             { get; init; }
        public uint                   TargetMask           { get; init; }
        public byte                   CommandCount         { get; init; }
        public uint[]?                CommandTargets       { get; init; }
        public ParryTimingMode        TimingMode           { get; init; }
        public float                  LeadSeconds          { get; init; }
        public float                  LegacyWindowSeconds  { get; init; }
        public float                  ResolveWindowSeconds { get; init; }
        public List<ParryTimingEvent> Events               { get; } = new();
        public float?                 EndSeconds           { get; set; }
        public string?                EndReason            { get; set; }
        public bool                   ParrySucceeded       { get; set; }
    }

    private sealed class ParryTimingEvent {
        public string Type        { get; init; } = string.Empty;
        public int    Slot        { get; init; }
        public float  TimeSeconds { get; init; }
    }
}

internal static class ImGuiNativeExtra {
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    public static extern void igSetWindowFontScale(float scale);
}


















