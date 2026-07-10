namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private static readonly Vector4 OverlayShadowColor = new(0f, 0f, 0f, 0.75f);
    private static readonly Vector4 OverlayOutlineColor = new(0f, 0f, 0f, 0.9f);

    private void render_parry_window_overlay()
    {
        if (_runtime.ParriedTextRemainingSeconds <= 0f) return;
        if (_runtime.LastParriedTargetMask == 0) return;
        float t = 1f - _runtime.ParriedTextRemainingSeconds / ParriedTextSeconds;
        render_combat_labels(_runtime.LastParriedTargetMask, "PARRIED", preciseTiming: true, t, _parriedTextSeed);
    }

    private void render_dodge_overlay()
    {
        if (_dodgeTextRemainingSeconds <= 0f) return;
        if (_dodgeTextTargetMask == 0) return;
        float t = 1f - _dodgeTextRemainingSeconds / ParriedTextSeconds;

        // Slots that evaded inside the tighter parry window read PERFECT and share the parry's
        // gold tint; both grant the overdrive boost. The rest stay DODGE in plain cream. Same
        // timer and seed, so a mixed group animates as one.
        uint perfect = _dodgeTextTargetMask & _dodgeTextPerfectMask;
        uint plain = _dodgeTextTargetMask & ~_dodgeTextPerfectMask;
        if (perfect != 0) render_combat_labels(perfect, "PERFECT", preciseTiming: true, t, _dodgeTextSeed);
        if (plain != 0) render_combat_labels(plain, "DODGE", preciseTiming: false, t, _dodgeTextSeed);
    }

    // Shared animated combat-label renderer. Fill colour comes from CombatLabelPalette:
    // PARRIED and PERFECT take the gold tint, DODGE stays cream. t = 0..1 progress over the
    // label lifetime (ParriedTextSeconds). Each targeted, on-field actor gets one label anchored
    // to its live engine-projected screen position, transformed (pop-in overshoot, squash, skew
    // kick, rotation, whip + float, fade) with two ghost echoes.
    private void render_combat_labels(uint mask, string text, bool preciseTiming, float t, float seed)
    {
        Vector2 displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= 1f || displaySize.Y <= 1f) return;

        bool hasCustomFont = try_get_selected_overlay_font(out ImFontPtr customFont);
        if (hasCustomFont) ImGui.PushFont(customFont, OverlayFontSizePx);
        Vector2 textSize = ImGui.CalcTextSize(text);
        if (hasCustomFont) ImGui.PopFont();

        ImDrawListPtr draw = ImGui.GetForegroundDrawList();

        while (mask != 0)
        {
            int slot = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;

            // Skip inactive / off-field slots (reserve party members have no live actor).
            Chr* slotChr = try_get_chr((byte)slot);
            if (slotChr == null || slotChr->actor == null || !slotChr->stat_exist_flag) continue;

            // Only draw when the actor has a valid live-camera projection; behind-camera / off
            // returns null and we skip (no mid-screen fallback clutter now that projection works).
            Vector2? anchor = try_get_parried_overlay_anchor((byte)slot, displaySize);
            if (anchor == null) continue;

            draw_animated_label(draw, customFont, hasCustomFont, anchor.Value, text, textSize, preciseTiming, t, seed, slot, displaySize);
        }
    }

    private void draw_animated_label(ImDrawListPtr draw, ImFontPtr font, bool hasFont, Vector2 anchor, string text, Vector2 textSize, bool preciseTiming, float t, float seed, int slot, Vector2 displaySize)
    {
        LabelAnim a = compute_label_anim(t, seed, slot);
        if (a.Alpha <= 0.02f) return;

        float posScale = displaySize.Y / 720f;   // px offsets authored against a 720-tall reference
        Vector4 fill = CombatLabelPalette.GetFill(preciseTiming);
        fill.W = a.Alpha;
        Vector4 outline = OverlayOutlineColor;
        outline.W = OverlayOutlineColor.W * a.Alpha;

        Vector2 pos = anchor - textSize * 0.5f;

        int vtxStart = draw.VtxBuffer.Size;
        draw_outlined_overlay_text(draw, font, hasFont, pos, text, fill, outline, 1.5f);
        int vtxEnd = draw.VtxBuffer.Size;

        transform_label_vertices(draw, vtxStart, vtxEnd, anchor, a, posScale);
    }

    // Applies the label's scale/squash + skew + rotation about the anchor pivot, plus the
    // translated whip/float offset, directly to the glyph vertices AddText just emitted (ImGui's
    // AddText can only place axis-aligned text, so the transform is done on the vertex buffer).
    private static void transform_label_vertices(ImDrawListPtr draw, int start, int end, Vector2 pivot, LabelAnim a, float posScale)
    {
        if (end <= start) return;
        float rad = a.Rotation * (MathF.PI / 180f);
        float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
        float tanX = MathF.Tan(a.SkewX * (MathF.PI / 180f));
        float tanY = MathF.Tan(a.SkewY * (MathF.PI / 180f));
        Vector2 translate = new(a.X * posScale, a.Y * posScale);

        ImDrawVert* vtx = (ImDrawVert*)draw.VtxBuffer.Data;
        int count = draw.VtxBuffer.Size;
        for (int i = start; i < end && i < count; i++)
        {
            Vector2 p = vtx[i].Pos - pivot;
            float rx = p.X * cos - p.Y * sin;
            float ry = p.X * sin + p.Y * cos;
            float sx = rx + tanX * ry;
            float sy = ry + tanY * rx;
            vtx[i].Pos = pivot + new Vector2(sx * a.ScaleX, sy * a.ScaleY) + translate;
        }
    }

    private readonly struct LabelAnim
    {
        public readonly float X, Y, ScaleX, ScaleY, Rotation, SkewX, SkewY, Alpha;
        public LabelAnim(float x, float y, float sx, float sy, float rot, float skx, float sky, float alpha)
        { X = x; Y = y; ScaleX = sx; ScaleY = sy; Rotation = rot; SkewX = skx; SkewY = sky; Alpha = alpha; }
    }

    private static float clamp01(float v) => Math.Clamp(v, 0f, 1f);
    private static float ease_out_cubic(float t) { t = clamp01(t); return 1f - MathF.Pow(1f - t, 3f); }
    private static float ease_in_cubic(float t) { t = clamp01(t); return t * t * t; }
    private static float ease_out_back(float t)
    {
        t = clamp01(t);
        const float c1 = 1.70158f, c3 = c1 + 1f;
        return 1f + c3 * MathF.Pow(t - 1f, 3f) + c1 * MathF.Pow(t - 1f, 2f);
    }
    private static float lerpf(float a, float b, float t) => a + (b - a) * clamp01(t);

    // Exp33-style DODGE/PARRIED: enter italic (skewed) and slightly small/narrow, de-skew to a
    // subtle residual lean and grow to full size over the first ~quarter, hold, then fade out. No
    // overshoot, no rotation, no whip, no ghost smear — restrained and elegant (matches the ref).
    private static LabelAnim compute_label_anim(float t, float seed, int slot)
    {
        // The strength lives in the ENTRY: extreme skew + a little rotation + italic, resolving
        // fast to the normal upright text; the hold stays calm, then it fades. FFX has no camera
        // shake, so each appearance perturbs its entry params a little (per seed + slot) for life.
        float r0 = hash01(seed, slot, 1);
        float r1 = hash01(seed, slot, 2);
        float r2 = hash01(seed, slot, 3);
        float r3 = hash01(seed, slot, 4);

        float introDur = 0.16f * lerpf(0.85f, 1.15f, r3);
        float intro = ease_out_cubic(t / introDur);
        float skewResolve = ease_out_cubic(t / (introDur * 1.25f));
        float rotResolve = ease_out_cubic(t / (introDur * 0.9f));
        float exit = ease_in_cubic((t - 0.70f) / 0.30f);

        float scaleStart = lerpf(0.68f, 0.78f, r0);
        float scale = lerpf(scaleStart, 1.0f, intro);
        float scaleX = scale * lerpf(0.84f, 1.0f, intro);   // horizontal unfurl on entry

        // Negative skewX = italic-right lean in transform_label_vertices. Extreme on entry,
        // resolving to a small residual italic lean (flip the sign if it leans wrong in-game).
        float skewX = lerpf(-30f * lerpf(0.85f, 1.2f, r0), lerpf(-8f, -4f, r2), skewResolve);
        float skewY = 0f;

        // A little rotation on entry, leveling out fast; magnitude/direction vary a touch.
        float rotation = lerpf(lerpf(-11f, 3f, r1), 0f, rotResolve);

        float x = 0f;
        float y = -exit * 10f;   // stays centered on the char; only lifts away as it fades out

        float fadeIn = ease_out_cubic(t / 0.05f);
        float fadeOut = 1f - ease_in_cubic((t - 0.72f) / 0.28f);
        float alpha = clamp01(fadeIn * fadeOut);

        return new LabelAnim(x, y, scaleX, scale, rotation, skewX, skewY, alpha);
    }

    // Cheap deterministic hash → [0,1) from (per-appearance seed, slot, salt), stable across the
    // label's lifetime so the randomized entry doesn't jitter frame-to-frame.
    private static float hash01(float seed, int slot, int salt)
    {
        float v = MathF.Sin(seed * 127.1f + slot * 311.7f + salt * 74.7f) * 43758.5453f;
        return v - MathF.Floor(v);
    }

    // Draws text with a solid outline (8 offsets in outlineColor) then the fill on top — a cleaner
    // contour than the single drop-shadow the parry overlay uses.
    private static void draw_outlined_overlay_text(ImDrawListPtr draw, ImFontPtr font, bool hasFont, Vector2 pos, string text, Vector4 fillColor, Vector4 outlineColor, float px)
    {
        uint oc = ImGui.ColorConvertFloat4ToU32(outlineColor);
        uint fc = ImGui.ColorConvertFloat4ToU32(fillColor);

        for (int i = 0; i < 8; i++)
        {
            float dx = i switch { 0 => -px, 1 => px, 2 => 0f, 3 => 0f, 4 => -px, 5 => px, 6 => -px, _ => px };
            float dy = i switch { 0 => 0f, 1 => 0f, 2 => -px, 3 => px, 4 => -px, 5 => -px, 6 => px, _ => px };
            Vector2 op = new(pos.X + dx, pos.Y + dy);
            if (hasFont) draw.AddText(font, OverlayFontSizePx, op, oc, text);
            else draw.AddText(op, oc, text);
        }

        if (hasFont) draw.AddText(font, OverlayFontSizePx, pos, fc, text);
        else draw.AddText(pos, fc, text);
    }

    // Diagnostic: throttled per-frame log of the overlay anchor projection outcome, to find why
    // DODGE/PARRIED land mid-screen (fallback). Gated on _optionLogging.
    private void overlay_probe_log(string msg)
    {
        if (!_optionLogging) return;
        if ((_debugFrameIndex % 6) != 0) return;
        log_debug($"[OverlayProbe] {msg}");
    }

    private Vector2? try_get_parried_overlay_anchor(byte slotIndex, Vector2 displaySize)
    {
        if (!try_get_live_battle_context(out _))
        {
            overlay_probe_log($"slot={slotIndex} no live battle context");
            return null;
        }

        Chr* chr = try_get_chr(slotIndex);
        if (chr == null || chr->actor == null)
        {
            overlay_probe_log($"slot={slotIndex} chr={(chr == null ? "null" : "ok")} actor={(chr != null && chr->actor != null ? "ok" : "null")}");
            return null;
        }
        if (!chr->stat_exist_flag || chr->ram.hp <= 0)
        {
            overlay_probe_log($"slot={slotIndex} exist={chr->stat_exist_flag} hp={(int)chr->ram.hp}");
            return null;
        }

        // MsCalcCursorPos (0x0079f3a0) projects every battle actor through the LIVE Phyre camera
        // once per battle-draw frame (MsBattleCursorCalc -> TODrawBtlWindow) and stores FIVE pairs
        // per Chr in a 512x416 virtual viewport. The four anchor/centre coordinate pairs are int
        // pixels; 0xf3c alone is read as a float — it carries the behind-camera sentinel, not a
        // coordinate:
        //   0xf34/0xf38  raw centre projection      — unclamped, no sentinel. Drifts far negative.
        //   0xf3c/0xf40  rounded centre             — 0xf3c carries the behind-camera sentinel
        //   0xf44/0xf48  camera anchor              — what MsNumberDrawProcess uses for damage
        //                                             numbers and MISS (FFX.exe.c:848303-848310)
        //   0xf4c/0xf50  same anchor, clamped to X 27..485, Y 34..391
        // We anchor on 0xf44/0xf48 so our labels land exactly where the engine draws its own
        // numbers, and fall back to the clamped pair when that point leaves the viewport. The
        // engine picks between the two on popup flag 0x80; we have no popup, so we range-test.
        // The legacy ms_camera_matrix VU0 path is dead on PC/HD and was removed with this change.
        const int F3C_Sentinel = 0xf3c;
        const int F44_AnchorX = 0xf44, F48_AnchorY = 0xf48;
        const int F4C_ClampedX = 0xf4c, F50_ClampedY = 0xf50;

        byte* b = (byte*)chr;
        float sentinel = *(float*)(b + F3C_Sentinel);
        if (OverlayAnchorMath.IsBehindCamera(sentinel))
        {
            // NaN and the engine sentinel mean different things (uninitialised actor vs. behind
            // the camera). Both are unusable, but keep them apart in the log so the next reader
            // is not misled the way the previous "behind-camera (f3c=NaN)" line misled us.
            string why = float.IsNaN(sentinel) ? "no-projection (f3c=NaN)" : $"behind-camera (f3c={sentinel:E2})";
            overlay_probe_log($"slot={slotIndex} {why}");
            return null;
        }

        int virtX = *(int*)(b + F44_AnchorX);
        int virtY = *(int*)(b + F48_AnchorY);
        bool clamped = false;
        if (!OverlayAnchorMath.IsWithinVirtualViewport(virtX, virtY))
        {
            virtX = *(int*)(b + F4C_ClampedX);
            virtY = *(int*)(b + F50_ClampedY);
            clamped = true;
        }

        // draw_animated_label centres the glyph box on the anchor (pos = anchor - textSize * 0.5f).
        Vector2 screen = OverlayAnchorMath.ToScreen(virtX, virtY, displaySize);

        overlay_probe_log(
            $"slot={slotIndex} ANCHOR{(clamped ? "(clamped)" : string.Empty)} virt=({virtX},{virtY}) "
            + $"screen=({screen.X:F0},{screen.Y:F0}) disp=({displaySize.X:F0}x{displaySize.Y:F0})");
        return screen;
    }
}
