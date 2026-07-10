namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsCameraSetRectFn(uint camId, uint mode, float* vec4);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AtelGetCameraWorkAdrsFn(int worker);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsGetBattleSceneFn();

    // Hand-controlled battle camera. Fly with the freecam (WASD move relative to the view, Q/E
    // rise/fall, right-mouse-drag turns), then one button saves that view as this encounter's start
    // camera. Once saved, the encounter opens and holds there; otherwise the game's default framing
    // is used (frozen after it settles). Writes via MsCameraSetRect, which only holds while the
    // AllTurns hard lock is suppressing the game's own writers.
    private bool _freecamActive;
    private int _battleCameraId;                       // 0 = not resolved this battle yet
    // Fallback framing: with no saved start camera, let the game frame the battle for this many
    // seconds after the camera resolves, THEN freeze the settled default. -1 = not started this battle.
    private const float CameraSettleGraceSeconds = 1.0f;
    private float _cameraSettleSeconds = -1f;
    private bool _currentEncounterHasAnchor;           // updated each frame; read by the writer hook
    private Vector3 _freecamPos = new(0f, 80f, 260f);
    private float _freecamYaw;
    private float _freecamPitch;
    private float _freecamMoveSpeed = 4f;
    private const float FreecamLookSpeed = 0.004f;     // radians per mouse pixel
    private MsCameraSetRectFn? _camSetRect;
    private AtelGetCameraWorkAdrsFn? _getCamWorkAdrs;
    private MsGetBattleSceneFn? _getBattleScene;

    // Saved start cameras (persisted), keyed by battle map + enemy constellation so each encounter
    // keeps its own. Held from battle start whenever one exists for the current encounter.
    private readonly Dictionary<string, AnchorPose> _cameraAnchors = new();

    // Zoom-punch: a quick dolly of the eye toward the look-at on a parry, easing back. Fraction of
    // the eye→target distance; whole-party punches harder. Only applies while a saved start camera
    // is being held (we own the eye then).
    private const float ZoomPunchDuration   = 0.35f;
    private const float ZoomPunchSingle     = 0.12f;
    private const float ZoomPunchWholeParty = 0.30f;
    private float _zoomPunchRemaining;
    private float _zoomPunchStrength;

    private const uint CameraEyeBank = 1;   // MsCameraSetRect mode: eye bank
    private const uint CameraRefBank = 0;   // look-at / ref bank

    private readonly struct AnchorPose
    {
        public readonly Vector3 Pos;
        public readonly float Yaw, Pitch;
        public AnchorPose(Vector3 pos, float yaw, float pitch) { Pos = pos; Yaw = yaw; Pitch = pitch; }
    }

    private void trigger_zoom_punch(bool wholeParty)
    {
        _zoomPunchRemaining = ZoomPunchDuration;
        _zoomPunchStrength  = wholeParty ? ZoomPunchWholeParty : ZoomPunchSingle;
    }

    private float zoom_punch_factor()
    {
        if (_zoomPunchRemaining <= 0f) return 0f;
        float n = _zoomPunchRemaining / ZoomPunchDuration;   // 1 → 0
        return _zoomPunchStrength * n * n;                    // snap in, ease out
    }

    // Cache the battle camera id from the ATEL camera work slot. It resolves only once the game has
    // run a real camera write; the writer hook lets writes through until then so it gets populated.
    private void capture_battle_camera_id(int worker)
    {
        if (worker == 0 || _battleCameraId != 0) return;
        _getCamWorkAdrs ??= FhUtil.get_fptr<AtelGetCameraWorkAdrsFn>(ExternalMemoryOffsetMap.Functions.AtelGetCameraWorkAdrs);
        int workAdrs = _getCamWorkAdrs(worker);
        if (workAdrs == 0) return;
        int camId = *(int*)workAdrs;
        if (camId != 0)
        {
            _battleCameraId = camId;
            _cameraSettleSeconds = CameraSettleGraceSeconds;
        }
    }

    // Per-frame drive. Freecam takes priority; otherwise a saved start camera for this encounter is
    // held. No-ops unless a battle is live and the camera id is resolved.
    private void drive_camera()
    {
        if (!try_get_live_battle_context(out _)) { _battleCameraId = 0; _cameraSettleSeconds = -1f; _currentEncounterHasAnchor = false; return; }
        if (_battleCameraId == 0) return;
        if (_cameraSettleSeconds > 0f) _cameraSettleSeconds = MathF.Max(0f, _cameraSettleSeconds - ImGui.GetIO().DeltaTime);
        if (_zoomPunchRemaining > 0f) _zoomPunchRemaining = MathF.Max(0f, _zoomPunchRemaining - ImGui.GetIO().DeltaTime);

        bool haveAnchor = _cameraAnchors.TryGetValue(current_battle_camera_key(), out AnchorPose anchor);
        _currentEncounterHasAnchor = haveAnchor;

        if (_freecamActive)
        {
            read_freecam_input();
            stamp_camera(_freecamPos, _freecamYaw, _freecamPitch);
            return;
        }

        if (haveAnchor)
        {
            Vector3 target = anchor_look_point(anchor);
            Vector3 eye = Vector3.Lerp(anchor.Pos, target, zoom_punch_factor());
            _camSetRect ??= FhUtil.get_fptr<MsCameraSetRectFn>(ExternalMemoryOffsetMap.Functions.MsCameraSetRect);
            write_camera_bank(CameraEyeBank, eye);
            write_camera_bank(CameraRefBank, target);
        }
    }

    private static Vector3 anchor_look_point(AnchorPose a)
    {
        float cp = MathF.Cos(a.Pitch), sp = MathF.Sin(a.Pitch);
        float cy = MathF.Cos(a.Yaw),   sy = MathF.Sin(a.Yaw);
        return a.Pos + new Vector3(cp * sy, sp, cp * cy);
    }

    private void write_camera_bank(uint mode, Vector3 v)
    {
        float* buf = stackalloc float[4];
        buf[0] = v.X; buf[1] = v.Y; buf[2] = v.Z; buf[3] = 0f;
        _camSetRect!((uint)_battleCameraId, mode, buf);
    }

    // Key for the current encounter: battle map id + the sorted enemy chr_ids, both game-defined so
    // the key is stable across visits.
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

        // Turn: right-mouse-drag (vertical inverted so drag-down looks down).
        if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            _freecamYaw   += io.MouseDelta.X * FreecamLookSpeed;
            _freecamPitch += io.MouseDelta.Y * FreecamLookSpeed;
        }
        _freecamPitch = Math.Clamp(_freecamPitch, -1.5f, 1.5f);

        float cp = MathF.Cos(_freecamPitch), sp = MathF.Sin(_freecamPitch);
        float cy = MathF.Cos(_freecamYaw),   sy = MathF.Sin(_freecamYaw);
        Vector3 fwd   = new(cp * sy, sp, cp * cy);
        Vector3 right = new(cy, 0f, -sy);

        if (ImGui.IsKeyDown(ImGuiKey.W)) _freecamPos += fwd * move;
        if (ImGui.IsKeyDown(ImGuiKey.S)) _freecamPos -= fwd * move;
        if (ImGui.IsKeyDown(ImGuiKey.D)) _freecamPos += right * move;
        if (ImGui.IsKeyDown(ImGuiKey.A)) _freecamPos -= right * move;
        if (ImGui.IsKeyDown(ImGuiKey.E)) _freecamPos.Y += move;
        if (ImGui.IsKeyDown(ImGuiKey.Q)) _freecamPos.Y -= move;
    }

    private void stamp_camera(Vector3 pos, float yaw, float pitch)
    {
        if (_battleCameraId == 0) return;
        _camSetRect ??= FhUtil.get_fptr<MsCameraSetRectFn>(ExternalMemoryOffsetMap.Functions.MsCameraSetRect);

        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        float cy = MathF.Cos(yaw),   sy = MathF.Sin(yaw);
        write_camera_bank(CameraEyeBank, pos);
        write_camera_bank(CameraRefBank, pos + new Vector3(cp * sy, sp, cp * cy));
    }

    // Camera tab: freecam + one button to save the current view as this encounter's start camera.
    private void render_camera_tab()
    {
        ImGui.Checkbox("Freecam##fhparry.freecam", ref _freecamActive);
        ImGui.SameLine();
        ImGui.TextDisabled(_battleCameraId == 0 ? "(camera not resolved yet)" : "WASD move · Q/E down-up · right-drag turn");
        ImGui.DragFloat("move speed##fhparry.freecam.mv", ref _freecamMoveSpeed, 0.1f, 0.1f, 40f);

        ImGui.Separator();
        string key = current_battle_camera_key();
        bool hasStart = _cameraAnchors.ContainsKey(key);

        if (ImGui.Button("Set current view as this battle's start camera##fhparry.cam.set"))
        {
            _cameraAnchors[key] = new AnchorPose(_freecamPos, _freecamYaw, _freecamPitch);
            persist_settings();
            log_debug($"[Camera] start position set for {key}.");
        }
        if (hasStart)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear##fhparry.cam.clear"))
            {
                _cameraAnchors.Remove(key);
                persist_settings();
                log_debug($"[Camera] start position cleared for {key}.");
            }
        }

        ImGui.TextDisabled(hasStart
            ? "This battle holds your saved camera from the start."
            : "This battle uses the game's default camera (frozen once it settles).");
    }
}
