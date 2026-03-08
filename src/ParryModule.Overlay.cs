namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private static readonly Vector4 OverlayTextColor = new(0.93f, 0.95f, 0.82f, 1.0f);
    private static readonly Vector4 OverlayShadowColor = new(0f, 0f, 0f, 0.75f);
    private static readonly Vector4 OverlayBackgroundColor = new(0f, 0f, 0f, 0.42f);
    private static readonly Vector4 StateTextDashColor = new(0.70f, 0.70f, 0.70f, 1.0f);
    private static readonly Vector4 StateTextWaitingColor = new(0.92f, 0.92f, 0.92f, 1.0f);
    private static readonly Vector4 StateTextOpenColor = new(1.0f, 1.0f, 1.0f, 1.0f);
    private static readonly Vector4 StateTextSucceededColor = new(0.28f, 0.92f, 0.42f, 1.0f);
    private static readonly Vector4 StateTextMissedColor = new(0.96f, 0.34f, 0.34f, 1.0f);
    private static readonly Vector4 StateBackgroundColor = new(0f, 0f, 0f, 0.45f);
    private const ulong OverlayProjectionRetryFrames = 120;

    private enum OverlayProjectionMode
    {
        Unknown = 0,
        CameraScreen,
        ScreenCamera,
        CameraScreenTransposed,
        ScreenCameraTransposed,
        CameraBackup,
        BackupCamera,
        CameraBackupTransposed,
        BackupCameraTransposed
    }

    private OverlayProjectionMode _overlayProjectionMode = OverlayProjectionMode.Unknown;
    private ulong _overlayProjectionLastSuccessFrame;

    private void render_parry_state_hud()
    {
        if (!_optionParryStateHud) return;
        if (!_debugGameplayReady) return;

        Vector2 displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= 1f || displaySize.Y <= 1f) return;

        (string label, Vector4 color) = resolve_parry_state_hud_display();

        bool hasCustomFont = try_get_selected_overlay_font(out ImFontPtr customFont);
        if (hasCustomFont)
        {
            ImGui.PushFont(customFont, OverlayFontSizePx);
        }

        Vector2 textSize = ImGui.CalcTextSize(label);
        if (hasCustomFont)
        {
            ImGui.PopFont();
        }

        Vector2 anchor = new(displaySize.X * 0.5f, displaySize.Y * 0.18f);
        Vector2 textPos = anchor - textSize * 0.5f;
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();

        Vector2 pad = new(16f, 10f);
        Vector2 bgMin = textPos - pad;
        Vector2 bgMax = textPos + textSize + pad;
        draw.AddRectFilled(bgMin, bgMax, ImGui.ColorConvertFloat4ToU32(StateBackgroundColor), 8f);

        Vector2 shadowOffset = new(2f, 2f);
        if (hasCustomFont)
        {
            draw.AddText(customFont, OverlayFontSizePx, textPos + shadowOffset, ImGui.ColorConvertFloat4ToU32(OverlayShadowColor), label);
            draw.AddText(customFont, OverlayFontSizePx, textPos, ImGui.ColorConvertFloat4ToU32(color), label);
        }
        else
        {
            draw.AddText(textPos + shadowOffset, ImGui.ColorConvertFloat4ToU32(OverlayShadowColor), label);
            draw.AddText(textPos, ImGui.ColorConvertFloat4ToU32(color), label);
        }
    }

    private (string Label, Vector4 Color) resolve_parry_state_hud_display()
    {
        if (!_optionEnabled)
        {
            return ("Disabled", StateTextDashColor);
        }

        if (!try_get_live_battle_context(out _))
        {
            return ("-", StateTextDashColor);
        }

        string tierLine = format_spam_tier_line();

        if (_runtime.ParriedTextRemainingSeconds > 0f)
        {
            return ("Succeeded" + tierLine, StateTextSucceededColor);
        }

        if (_runtime.ParryMissedTextRemainingSeconds > 0f)
        {
            return ("Missed" + tierLine, StateTextMissedColor);
        }

        if (_runtime.ParryWindowActive)
        {
            return ("Open" + tierLine, StateTextOpenColor);
        }

        if (_runtime.AwaitingTurnEnd && _runtime.CurrentPartyTargetMask != 0)
        {
            return ("Waiting" + tierLine, StateTextWaitingColor);
        }

        return ("-" + tierLine, StateTextDashColor);
    }

    private string format_spam_tier_line()
    {
        if (!_optionPenaltyEnabled)
        {
            return "\nPenalty Off";
        }

        int tierIndex = _spamController.TierIndex;

        if (tierIndex >= ParryDifficultyModel.MaxSpamTierIndex)
        {
            return "\nPENALTY";
        }

        if (tierIndex == 0)
        {
            return string.Empty;
        }

        float remaining = _spamController.CalmResetRemainingSeconds;
        if (remaining > 0f)
        {
            return $"\nT{tierIndex + 1} {remaining:F1}s";
        }

        return string.Empty;
    }

    private void render_parry_window_overlay()
    {
        if (_runtime.ParriedTextRemainingSeconds <= 0f) return;
        if (_runtime.LastParriedTargetSlot < 0) return;

        Vector2 displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= 1f || displaySize.Y <= 1f) return;

        bool hasCustomFont = try_get_selected_overlay_font(out ImFontPtr customFont);
        if (hasCustomFont)
        {
            ImGui.PushFont(customFont, OverlayFontSizePx);
        }

        Vector2 textSize = ImGui.CalcTextSize("PARRIED");
        if (hasCustomFont)
        {
            ImGui.PopFont();
        }

        Vector2 anchor = try_get_parried_overlay_anchor((byte)_runtime.LastParriedTargetSlot, displaySize)
            ?? get_fallback_overlay_anchor(_runtime.LastParriedTargetSlot, displaySize);

        Vector2 textPos = anchor - textSize * 0.5f;
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();

        Vector2 pad = new(14f, 10f);
        Vector2 bgMin = textPos - pad;
        Vector2 bgMax = textPos + textSize + pad;
        draw.AddRectFilled(bgMin, bgMax, ImGui.ColorConvertFloat4ToU32(OverlayBackgroundColor), 8f);

        Vector2 shadowOffset = new(2f, 2f);
        if (hasCustomFont)
        {
            draw.AddText(customFont, OverlayFontSizePx, textPos + shadowOffset, ImGui.ColorConvertFloat4ToU32(OverlayShadowColor), "PARRIED");
            draw.AddText(customFont, OverlayFontSizePx, textPos, ImGui.ColorConvertFloat4ToU32(OverlayTextColor), "PARRIED");
        }
        else
        {
            draw.AddText(textPos + shadowOffset, ImGui.ColorConvertFloat4ToU32(OverlayShadowColor), "PARRIED");
            draw.AddText(textPos, ImGui.ColorConvertFloat4ToU32(OverlayTextColor), "PARRIED");
        }
    }

    private Vector2? try_get_parried_overlay_anchor(byte slotIndex, Vector2 displaySize)
    {
        if (!try_get_live_battle_context(out _)) return null;

        Chr* chr = try_get_chr(slotIndex);
        if (chr == null || chr->actor == null) return null;
        if (!chr->stat_exist_flag || chr->ram.hp <= 0) return null;

        Vector3 worldPos = new(
            chr->actor->chr_pos_vec.X,
            chr->actor->chr_pos_vec.Y + 18f,
            chr->actor->chr_pos_vec.Z);

        if (try_project_world_to_screen(worldPos, displaySize, out Vector2 projected))
        {
            return projected;
        }

        return null;
    }

    private static Vector2 get_fallback_overlay_anchor(int slotIndex, Vector2 displaySize)
    {
        int clamped = Math.Clamp(slotIndex, 0, 2);
        float x = displaySize.X * (0.34f + clamped * 0.085f);
        float y = displaySize.Y * (0.56f - clamped * 0.035f);
        return new Vector2(x, y);
    }

    private bool try_project_world_to_screen(Vector3 worldPos, Vector2 displaySize, out Vector2 screenPos)
    {
        screenPos = default;

        if (!try_read_projection_matrices(out Matrix4x4 camera, out Matrix4x4 screen, out Matrix4x4 cameraBackup, out Matrix4x4 screenBackup))
        {
            return false;
        }

        // Use the last successful mode first to keep projection stable frame-to-frame.
        if (_overlayProjectionMode != OverlayProjectionMode.Unknown
            && try_project_with_mode(_overlayProjectionMode, worldPos, displaySize, camera, screen, cameraBackup, screenBackup, out screenPos))
        {
            _overlayProjectionLastSuccessFrame = _debugFrameIndex;
            return true;
        }

        // Avoid expensive fallback scanning every frame if we already have a known-good mode.
        bool allowRescan = _overlayProjectionMode == OverlayProjectionMode.Unknown
            || _debugFrameIndex - _overlayProjectionLastSuccessFrame >= OverlayProjectionRetryFrames;
        if (!allowRescan)
        {
            return false;
        }

        OverlayProjectionMode[] candidates = [
            OverlayProjectionMode.CameraScreen,
            OverlayProjectionMode.ScreenCamera,
            OverlayProjectionMode.CameraScreenTransposed,
            OverlayProjectionMode.ScreenCameraTransposed,
            OverlayProjectionMode.CameraBackup,
            OverlayProjectionMode.BackupCamera,
            OverlayProjectionMode.CameraBackupTransposed,
            OverlayProjectionMode.BackupCameraTransposed
        ];

        for (int i = 0; i < candidates.Length; i++)
        {
            OverlayProjectionMode mode = candidates[i];
            if (!try_project_with_mode(mode, worldPos, displaySize, camera, screen, cameraBackup, screenBackup, out screenPos))
            {
                continue;
            }

            _overlayProjectionMode = mode;
            _overlayProjectionLastSuccessFrame = _debugFrameIndex;
            return true;
        }

        return false;
    }

    private static bool try_read_projection_matrices(
        out Matrix4x4 camera,
        out Matrix4x4 screen,
        out Matrix4x4 cameraBackup,
        out Matrix4x4 screenBackup)
    {
        camera = default;
        screen = default;
        cameraBackup = default;
        screenBackup = default;

        uint* rawCamera = FhFfx.FhCall.ms_camera_matrix;
        uint* rawScreen = FhFfx.FhCall.ms_screen_matrix;
        uint* rawCameraBackup = FhFfx.FhCall.ms_camera_matrix_backup;
        uint* rawScreenBackup = FhFfx.FhCall.ms_screen_matrix_backup;
        if (rawCamera == null || rawScreen == null)
        {
            return false;
        }

        camera = read_matrix((float*)rawCamera);
        screen = read_matrix((float*)rawScreen);
        cameraBackup = rawCameraBackup != null ? read_matrix((float*)rawCameraBackup) : camera;
        screenBackup = rawScreenBackup != null ? read_matrix((float*)rawScreenBackup) : screen;
        return true;
    }

    private static bool try_project_with_mode(
        OverlayProjectionMode mode,
        Vector3 worldPos,
        Vector2 displaySize,
        Matrix4x4 camera,
        Matrix4x4 screen,
        Matrix4x4 cameraBackup,
        Matrix4x4 screenBackup,
        out Vector2 screenPos)
    {
        Matrix4x4 first;
        Matrix4x4 second;

        switch (mode)
        {
            case OverlayProjectionMode.CameraScreen:
                first = camera;
                second = screen;
                break;
            case OverlayProjectionMode.ScreenCamera:
                first = screen;
                second = camera;
                break;
            case OverlayProjectionMode.CameraScreenTransposed:
                first = Matrix4x4.Transpose(camera);
                second = Matrix4x4.Transpose(screen);
                break;
            case OverlayProjectionMode.ScreenCameraTransposed:
                first = Matrix4x4.Transpose(screen);
                second = Matrix4x4.Transpose(camera);
                break;
            case OverlayProjectionMode.CameraBackup:
                first = camera;
                second = screenBackup;
                break;
            case OverlayProjectionMode.BackupCamera:
                first = screenBackup;
                second = camera;
                break;
            case OverlayProjectionMode.CameraBackupTransposed:
                first = Matrix4x4.Transpose(camera);
                second = Matrix4x4.Transpose(screenBackup);
                break;
            case OverlayProjectionMode.BackupCameraTransposed:
                first = Matrix4x4.Transpose(screenBackup);
                second = Matrix4x4.Transpose(camera);
                break;
            default:
                first = cameraBackup;
                second = screenBackup;
                break;
        }

        return try_project_variant(worldPos, displaySize, first, second, out screenPos);
    }

    private static bool try_project_variant(
        Vector3 worldPos,
        Vector2 displaySize,
        Matrix4x4 first,
        Matrix4x4 second,
        out Vector2 screenPos)
    {
        screenPos = default;

        Vector4 v = new(worldPos, 1f);
        Vector4 clip = Vector4.Transform(v, first);
        clip = Vector4.Transform(clip, second);

        if (MathF.Abs(clip.W) < 0.0001f)
        {
            return false;
        }
        if (clip.W <= 0f) return false;

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;

        if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY))
        {
            return false;
        }

        float screenX = (ndcX * 0.5f + 0.5f) * displaySize.X;
        float screenY = (1f - (ndcY * 0.5f + 0.5f)) * displaySize.Y;
        if (!float.IsFinite(screenX) || !float.IsFinite(screenY))
        {
            return false;
        }

        if (screenX < -displaySize.X * 0.25f || screenX > displaySize.X * 1.25f) return false;
        if (screenY < -displaySize.Y * 0.25f || screenY > displaySize.Y * 1.25f) return false;

        screenPos = new Vector2(screenX, screenY);
        return true;
    }

    private static Matrix4x4 read_matrix(float* raw)
    {
        return new Matrix4x4(
            raw[0], raw[1], raw[2], raw[3],
            raw[4], raw[5], raw[6], raw[7],
            raw[8], raw[9], raw[10], raw[11],
            raw[12], raw[13], raw[14], raw[15]);
    }
}
