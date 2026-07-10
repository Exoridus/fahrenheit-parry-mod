namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsCameraSetRectFn(uint camId, uint mode, float* vec4);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AtelGetCameraWorkAdrsFn(int worker);

    // Battle camera control. Two behaviours:
    //   - Static hold: in AllTurns the hard lock freezes the game's own battle camera (after a short
    //     settle grace so it frames the battle at its correct default first), which is what keeps it
    //     rock-solid through ability pans/zooms — see should_hold_camera and the writer/request hooks.
    //   - Freecam: fly the camera by hand (WASD move relative to the view, Q/E rise/fall, right-mouse
    //     turns). Writes via MsCameraSetRect, which only holds while the hard lock suppresses the
    //     game's own writers. Axis handedness is a first guess, tuned in-game.
    private bool _freecamActive;
    private int _battleCameraId;                       // 0 = not resolved this battle yet
    // With no freecam, let the game frame the battle for this many seconds after the camera resolves,
    // THEN hold. -1 = not started this battle.
    private const float CameraSettleGraceSeconds = 1.0f;
    private float _cameraSettleSeconds = -1f;
    private Vector3 _freecamPos = new(0f, 80f, 260f);
    private float _freecamYaw;
    private float _freecamPitch;
    private float _freecamMoveSpeed = 4f;
    private const float FreecamLookSpeed = 0.004f;     // radians per mouse pixel
    private MsCameraSetRectFn? _camSetRect;
    private AtelGetCameraWorkAdrsFn? _getCamWorkAdrs;

    private const uint CameraEyeBank = 1;   // MsCameraSetRect mode: eye bank
    private const uint CameraRefBank = 0;   // look-at / ref bank

    // Cache the battle camera id from the ATEL camera work slot. It resolves only once the game has
    // run a real camera write; the writer/request hooks let activity through until then so it populates.
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

    // Per-frame drive. Only the freecam writes the camera; the static hold is done by suppressing the
    // game's writers, not by re-stamping. No-ops unless a battle is live and the camera id resolved.
    private void drive_camera()
    {
        if (!try_get_live_battle_context(out _)) { _battleCameraId = 0; _cameraSettleSeconds = -1f; return; }
        if (_battleCameraId == 0) return;
        if (_cameraSettleSeconds > 0f) _cameraSettleSeconds = MathF.Max(0f, _cameraSettleSeconds - ImGui.GetIO().DeltaTime);

        if (_freecamActive)
        {
            read_freecam_input();
            stamp_camera(_freecamPos, _freecamYaw, _freecamPitch);
        }
    }

    private void write_camera_bank(uint mode, Vector3 v)
    {
        float* buf = stackalloc float[4];
        buf[0] = v.X; buf[1] = v.Y; buf[2] = v.Z; buf[3] = 0f;
        _camSetRect!((uint)_battleCameraId, mode, buf);
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

    private void render_camera_tab()
    {
        ImGui.TextDisabled("AllTurns keeps the battle camera static. The freecam lets you fly it by hand.");
        ImGui.Checkbox("Freecam##fhparry.freecam", ref _freecamActive);
        ImGui.SameLine();
        ImGui.TextDisabled(_battleCameraId == 0 ? "(camera not resolved yet)" : "WASD move · Q/E down-up · right-drag turn");
        ImGui.DragFloat("move speed##fhparry.freecam.mv", ref _freecamMoveSpeed, 0.1f, 0.1f, 40f);
    }
}
