namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule {
    private void render_parry_window_overlay() {
        if (!_optionIndicator) return;

        float visibility = compute_overlay_animation_progress();
        bool showWindow = _runtime.OverlayState != ParryOverlayState.Hidden;
        if (!showWindow && visibility <= 0.01f) return;

        ensure_banner_textures();

        ParryOverlayState displayState = showWindow ? _runtime.OverlayState : _runtime.LastOverlayState;
        if (displayState == ParryOverlayState.Hidden) return;

        FhTexture? texture = displayState switch {
            ParryOverlayState.Parry => _bannerTextureParry,
            ParryOverlayState.Success => _bannerTextureSuccess,
            _ => _bannerTextureFail
        };

        Vector2 imageSize = texture != null
            ? new Vector2((float)texture.Metadata.width, (float)texture.Metadata.height)
            : new Vector2(640f, 260f);

        Vector2 windowSize = texture != null ? imageSize : imageSize + new Vector2(120f, 80f);
        float scale = compute_eased_scale(_runtime.OverlayScaleProgress);

        Vector2 displaySize = ImGui.GetIO().DisplaySize;
        Vector2 anchor = new(displaySize.X * 0.5f, displaySize.Y * 0.32f);

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
                // Fallback text overlay if external PNG resources are missing.
                Vector4 color = displayState switch {
                    ParryOverlayState.Parry => new Vector4(1f, 1f, 0.2f, 1f),
                    ParryOverlayState.Success => new Vector4(0.2f, 0.95f, 0.2f, 1f),
                    _ => new Vector4(0.95f, 0.2f, 0.2f, 1f)
                };

                ImGui.SetCursorPos(new Vector2(40f, windowSize.Y * 0.35f));
                ImGuiNativeExtra.igSetWindowFontScale(2.4f * scale);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                string label = displayState switch {
                    ParryOverlayState.Parry => "PARRY",
                    ParryOverlayState.Success => "SUCCESS",
                    _ => "MISSED"
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

    private void ensure_banner_textures() {
        if (_bannerTextureParry == null)
            _bannerTextureParry = try_load_banner_texture(_bannerPathParry, "parry.png", ref _bannerParryWarned);
        if (_bannerTextureSuccess == null)
            _bannerTextureSuccess = try_load_banner_texture(_bannerPathSuccess, "success.png", ref _bannerSuccessWarned);
        if (_bannerTextureFail == null)
            _bannerTextureFail = try_load_banner_texture(_bannerPathFail, "toobad.png", ref _bannerFailWarned);
    }

    private FhTexture? try_load_banner_texture(string? path, string label, ref bool warned) {
        if (string.IsNullOrWhiteSpace(path)) return null;

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

    private void update_overlay_animation_state() {
        ParryOverlayState nextState = _runtime.ParryWindowActive
            ? ParryOverlayState.Parry
            : (_runtime.SuccessIndicatorActive || _runtime.SuccessFlashFrames > 0)
                ? ParryOverlayState.Success
                : _runtime.FailureFlashFrames > 0
                    ? ParryOverlayState.Failure
                    : ParryOverlayState.Hidden;

        if (nextState != _runtime.OverlayState) {
            _runtime.OverlayState = nextState;
            if (_runtime.OverlayState != ParryOverlayState.Hidden) {
                _runtime.LastOverlayState = _runtime.OverlayState;
                _runtime.OverlayScaleProgress = 0f;
            }
        }

        float targetVisibility = _runtime.OverlayState == ParryOverlayState.Hidden ? 0f : 1f;
        float visibilityDelta = FrameDurationSeconds / OverlayAnimDurationSeconds;
        if (targetVisibility > _runtime.OverlayAnimProgress)
            _runtime.OverlayAnimProgress = MathF.Min(1f, _runtime.OverlayAnimProgress + visibilityDelta);
        else
            _runtime.OverlayAnimProgress = MathF.Max(0f, _runtime.OverlayAnimProgress - visibilityDelta);

        if (_runtime.OverlayState != ParryOverlayState.Hidden) {
            float scaleDelta = FrameDurationSeconds / OverlayScaleDurationSeconds;
            _runtime.OverlayScaleProgress = MathF.Min(1f, _runtime.OverlayScaleProgress + scaleDelta);
        }
    }

    private float compute_overlay_animation_progress() {
        return _runtime.OverlayAnimProgress;
    }

    private float compute_eased_scale(float progress) {
        float eased = evaluate_cubic_bezier(progress, 1.6f, 0.8f);
        return 0.5f + 0.5f * Math.Clamp(eased, 0f, 1.2f);
    }

    private static float evaluate_cubic_bezier(float t, float p1y, float p2y) {
        float clamped = Math.Clamp(t, 0f, 1f);
        float inv = 1f - clamped;
        return inv * inv * inv * 0f
             + 3f * inv * inv * clamped * p1y
             + 3f * inv * clamped * clamped * p2y
             + clamped * clamped * clamped;
    }
}

