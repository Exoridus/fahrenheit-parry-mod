namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsCameraSetRectFn(uint camId, uint mode, float* vec4);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AtelGetCameraWorkAdrsFn(int worker);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsGetBattleSceneFn();

    // Experimental hand-controlled battle camera. An FPS freecam: WASD move relative to where the
    // camera looks (W/S dolly along the view, A/D strafe), Q/E rise/fall, right-mouse-drag (or the
    // arrow keys) turns yaw/pitch. It writes the eye and look-at banks every frame via
    // MsCameraSetRect, so it only holds if the AllTurns hard lock is suppressing the game's own
    // writers — otherwise they fight it. The point is to find a fixed "E33" angle by feel and read
    // the coordinates off the panel, to hard-code later. Axis signs/handedness are a first guess and
    // meant to be tuned in-game.
    private bool _freecamActive;
    private int _battleCameraId;                       // 0 = not resolved this battle yet
    private Vector3 _freecamPos = new(0f, 80f, 260f);  // eye position; a guess, tune in-game
    private float _freecamYaw;                          // radians, horizontal turn
    private float _freecamPitch;                        // radians, vertical turn
    private float _freecamMoveSpeed = 4f;               // units per 30-fps frame
    private float _freecamLookSpeed = 0.004f;           // radians per mouse pixel
    private MsCameraSetRectFn? _camSetRect;
    private AtelGetCameraWorkAdrsFn? _getCamWorkAdrs;

    // Saved anchor poses (persisted), keyed by battle map + enemy constellation so each encounter
    // gets its own hand-tuned camera. When the anchor toggle is on, the pose matching the current
    // encounter is stamped every frame from battle start — a static "E33" camera that also snaps
    // back after any effect pan. Set it from the current freecam pose with the panel button.
    private bool _optionStaticCameraAnchor;
    private readonly Dictionary<string, AnchorPose> _cameraAnchors = new();
    private MsGetBattleSceneFn? _getBattleScene;

    private readonly struct AnchorPose
    {
        public readonly Vector3 Pos;
        public readonly float Yaw, Pitch;
        public AnchorPose(Vector3 pos, float yaw, float pitch) { Pos = pos; Yaw = yaw; Pitch = pitch; }
    }

    private const uint CameraEyeBank = 1;   // MsCameraSetRect mode: camSetPos (eye) bank
    private const uint CameraRefBank = 0;   // the look-at / ref bank

    // Cache the battle camera id from the ATEL camera work slot. The slot holds the resolved id only
    // once the game has run a real camera write; while it is 0 the writer hook lets the write through
    // (rather than suppressing) so the game resolves it, then we capture it here.
    private void capture_battle_camera_id(int worker)
    {
        if (worker == 0 || _battleCameraId != 0) return;
        _getCamWorkAdrs ??= FhUtil.get_fptr<AtelGetCameraWorkAdrsFn>(ExternalMemoryOffsetMap.Functions.AtelGetCameraWorkAdrs);
        int workAdrs = _getCamWorkAdrs(worker);
        if (workAdrs == 0) return;
        int camId = *(int*)workAdrs;
        if (camId != 0) _battleCameraId = camId;
    }

    // Per-frame drive. Freecam (user-controlled) takes priority; otherwise, if the anchor toggle is
    // on, the saved pose is held. Both no-op unless a battle is live and the camera id is resolved.
    private void drive_camera()
    {
        if (!try_get_live_battle_context(out _)) { _battleCameraId = 0; return; }
        if (_battleCameraId == 0) return;

        if (_freecamActive)
        {
            read_freecam_input();
            stamp_camera(_freecamPos, _freecamYaw, _freecamPitch);
        }
        else if (_optionStaticCameraAnchor && _cameraAnchors.TryGetValue(current_battle_camera_key(), out AnchorPose a))
        {
            stamp_camera(a.Pos, a.Yaw, a.Pitch);
        }
    }

    // Key for the current encounter: battle map id + the sorted enemy chr_ids. Same map with a
    // different enemy formation keys to a different anchor. The game defines both deterministically,
    // so the key is stable across visits.
    private string current_battle_camera_key()
    {
        _getBattleScene ??= FhUtil.get_fptr<MsGetBattleSceneFn>(ExternalMemoryOffsetMap.Functions.MsGetBattleScene);
        return $"{_getBattleScene():X8}|{format_enemy_constellation()}";
    }

    private string format_enemy_constellation()
    {
        Chr* enemies = _battleAdapter.GetMonsterCharacters();
        if (enemies == null) return "none";
        List<ushort> ids = [];
        for (int i = 0; i < EnemyActorCapacity; i++)
        {
            Chr* e = enemies + i;
            if (e->stat_exist_flag) ids.Add(e->chr_id);
        }
        if (ids.Count == 0) return "none";
        ids.Sort();
        return string.Join("-", ids.Select(static id => id.ToString("X4")));
    }

    private void read_freecam_input()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        float move = _freecamMoveSpeed * MathF.Max(io.DeltaTime * 30f, 0.5f);

        // Turn: right-mouse-drag. Vertical is inverted so drag-down looks down.
        if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            _freecamYaw   += io.MouseDelta.X * _freecamLookSpeed;
            _freecamPitch += io.MouseDelta.Y * _freecamLookSpeed;
        }
        _freecamPitch = Math.Clamp(_freecamPitch, -1.5f, 1.5f);

        float cp = MathF.Cos(_freecamPitch), sp = MathF.Sin(_freecamPitch);
        float cy = MathF.Cos(_freecamYaw),   sy = MathF.Sin(_freecamYaw);
        Vector3 fwd   = new(cp * sy, sp, cp * cy);
        Vector3 right = new(cy, 0f, -sy);

        // WASD move (right-mouse turns). It reads best natively; the trade-off is that battle
        // hotkeys on these keys (e.g. A swaps a party member) still fire underneath.
        if (ImGui.IsKeyDown(ImGuiKey.W)) _freecamPos += fwd * move;
        if (ImGui.IsKeyDown(ImGuiKey.S)) _freecamPos -= fwd * move;
        if (ImGui.IsKeyDown(ImGuiKey.D)) _freecamPos += right * move;
        if (ImGui.IsKeyDown(ImGuiKey.A)) _freecamPos -= right * move;
        if (ImGui.IsKeyDown(ImGuiKey.E)) _freecamPos.Y += move;
        if (ImGui.IsKeyDown(ImGuiKey.Q)) _freecamPos.Y -= move;
    }

    // Stamp a pose (eye + look-at) into the camera. Look direction from yaw/pitch; handedness is a
    // guess (tune in-game). MsCameraSetRect no-ops on a bad id, so a stale id cannot crash.
    private void stamp_camera(Vector3 pos, float yaw, float pitch)
    {
        if (_battleCameraId == 0) return;
        _camSetRect ??= FhUtil.get_fptr<MsCameraSetRectFn>(ExternalMemoryOffsetMap.Functions.MsCameraSetRect);

        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        float cy = MathF.Cos(yaw),   sy = MathF.Sin(yaw);
        Vector3 target = pos + new Vector3(cp * sy, sp, cp * cy);

        float* buf = stackalloc float[4];
        buf[0] = pos.X; buf[1] = pos.Y; buf[2] = pos.Z; buf[3] = 0f;
        _camSetRect((uint)_battleCameraId, CameraEyeBank, buf);
        buf[0] = target.X; buf[1] = target.Y; buf[2] = target.Z; buf[3] = 0f;
        _camSetRect((uint)_battleCameraId, CameraRefBank, buf);
    }

    // Lab-tab panel: toggle, live coordinates (copy them once the angle looks right), and drag fields
    // for fine tuning without the keyboard.
    private void render_freecam_panel()
    {
        if (ImGui.Checkbox("Freecam (experimental)##fhparry.freecam", ref _freecamActive) && _freecamActive)
        {
            log_debug("[Freecam] on. Needs AllTurns hard lock. WASD move, Q/E up-down, right-drag or arrows to turn.");
        }
        ImGui.SameLine();
        ImGui.TextDisabled(_battleCameraId == 0 ? "camId: unresolved" : $"camId: {_battleCameraId}");

        if (ImGui.Button("Log angle + map##fhparry.freecam.log")) capture_freecam_angle();
        ImGui.SameLine();
        ImGui.TextDisabled("appends to fhparry_freecam_captures.txt (next to the config)");

        ImGui.DragFloat("move speed##fhparry.freecam.mv", ref _freecamMoveSpeed, 0.1f, 0.1f, 40f);
        ImGui.DragFloat3("pos##fhparry.freecam.pos", ref _freecamPos, 0.5f);
        ImGui.DragFloat("yaw##fhparry.freecam.yaw", ref _freecamYaw, 0.01f);
        ImGui.DragFloat("pitch##fhparry.freecam.pitch", ref _freecamPitch, 0.01f);
        ImGui.TextDisabled("WASD move · Q down / E up · right-drag turn (V inverted).");

        ImGui.Separator();
        if (ImGui.Checkbox("Hold anchor from battle start##fhparry.freecam.anchor", ref _optionStaticCameraAnchor))
            persist_settings();
        ImGui.SameLine();
        if (ImGui.Button("Save pose for this encounter##fhparry.freecam.setanchor"))
        {
            string key = current_battle_camera_key();
            _cameraAnchors[key] = new AnchorPose(_freecamPos, _freecamYaw, _freecamPitch);
            persist_settings();
            log_debug($"[Freecam] anchor saved for {key}: pos=({_freecamPos.X:F2},{_freecamPos.Y:F2},{_freecamPos.Z:F2}) yaw={_freecamYaw:F4} pitch={_freecamPitch:F4}");
        }
        bool hasAnchor = _cameraAnchors.ContainsKey(current_battle_camera_key());
        ImGui.TextDisabled($"encounter: {current_battle_camera_key()}  ·  this one: {(hasAnchor ? "saved" : "none")}  ·  {_cameraAnchors.Count} total");
        ImGui.TextDisabled("Keyed by map + enemy group. Holds even with freecam off; snaps back after effect pans.");
    }

    // Persist the current camera pose and the battle map it was found on, so a hand-tuned angle is
    // not lost. Writes to the session log (visible) and appends one line to a durable captures file
    // next to the config (survives across sessions).
    private void capture_freecam_angle()
    {
        string line =
            $"{current_battle_camera_key()} pos=({_freecamPos.X:F2},{_freecamPos.Y:F2},{_freecamPos.Z:F2}) "
            + $"yaw={_freecamYaw:F4} pitch={_freecamPitch:F4} camId={_battleCameraId} frame=F{_debugFrameIndex:D7}";
        log_debug($"[Freecam] CAPTURED {line}");

        try
        {
            string dir = Path.GetDirectoryName(_settingsFilePath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string path = Path.Combine(dir, "fhparry_freecam_captures.txt");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            log_debug($"[Freecam] Could not persist capture: {ex.Message}");
        }
    }
}
