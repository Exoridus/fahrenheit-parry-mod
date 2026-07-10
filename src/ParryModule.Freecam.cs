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

    // Follow-cam (persisted): keep the static eye (anchor if set, else the frozen default battle
    // camera) but swing the look-at onto the acting enemy, damped, and ease back to the resting gaze
    // when no enemy is acting. Works with or without a saved anchor.
    private bool _optionFollowCam;
    private Vector3 _followLook;
    private bool _followInit;

    // Zoom-punch: a quick dolly of the eye toward the look-at on a parry, easing back. Value is a
    // fraction of the eye→target distance; whole-party punches harder, a single parry is subtle.
    // Only applies while we own the eye (anchor / follow-with-anchor) — not raw freecam.
    private const float ZoomPunchDuration    = 0.35f;
    private const float ZoomPunchSingle      = 0.12f;
    private const float ZoomPunchWholeParty  = 0.30f;
    private float _zoomPunchRemaining;
    private float _zoomPunchStrength;

    private void trigger_zoom_punch(bool wholeParty)
    {
        _zoomPunchRemaining = ZoomPunchDuration;
        _zoomPunchStrength  = wholeParty ? ZoomPunchWholeParty : ZoomPunchSingle;
    }

    private void tick_zoom_punch()
    {
        if (_zoomPunchRemaining > 0f)
            _zoomPunchRemaining = MathF.Max(0f, _zoomPunchRemaining - ImGui.GetIO().DeltaTime);
    }

    private float zoom_punch_factor()
    {
        if (_zoomPunchRemaining <= 0f) return 0f;
        float n = _zoomPunchRemaining / ZoomPunchDuration;   // 1 → 0
        return _zoomPunchStrength * n * n;                    // snap in, ease out
    }

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
        if (!try_get_live_battle_context(out _)) { _battleCameraId = 0; _followInit = false; return; }
        if (_battleCameraId == 0) return;
        tick_zoom_punch();

        if (_freecamActive)
        {
            read_freecam_input();
            stamp_camera(_freecamPos, _freecamYaw, _freecamPitch);
            return;
        }

        AnchorPose anchor = default;
        bool haveAnchor = _optionStaticCameraAnchor
            && _cameraAnchors.TryGetValue(current_battle_camera_key(), out anchor);

        if (_optionFollowCam)
        {
            // Aim at the acting enemy; ease back to the anchor's resting gaze when none is acting.
            Vector3 desired;
            if (try_get_attacker_world_pos(out Vector3 atkPos)) desired = atkPos;
            else if (haveAnchor) desired = anchor_look_point(anchor);
            else return;  // no attacker and no anchor default — nothing to aim at yet

            float k = 1f - MathF.Exp(-ImGui.GetIO().DeltaTime / 0.18f);
            _followLook = _followInit ? Vector3.Lerp(_followLook, desired, k) : desired;
            _followInit = true;

            _camSetRect ??= FhUtil.get_fptr<MsCameraSetRectFn>(ExternalMemoryOffsetMap.Functions.MsCameraSetRect);
            // else keep the frozen default eye; with an anchor, the zoom-punch dollies it toward the gaze.
            if (haveAnchor) write_camera_bank(CameraEyeBank, Vector3.Lerp(anchor.Pos, _followLook, zoom_punch_factor()));
            write_camera_bank(CameraRefBank, _followLook);
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

    // World position of the current attacker while an enemy is acting, from Chr->actor->chr_pos_vec.
    private bool try_get_attacker_world_pos(out Vector3 pos)
    {
        pos = default;
        if (!_runtime.AwaitingTurnEnd) return false;
        int atk = _runtime.CurrentAttackerId;
        if (atk < PartyActorCapacity) return false;   // enemies only
        Chr* chr = try_get_chr((byte)atk);
        if (chr == null || chr->actor == null) return false;
        Vector4 p = chr->actor->chr_pos_vec;
        pos = new Vector3(p.X, p.Y, p.Z);
        return true;
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
        write_camera_bank(CameraEyeBank, pos);
        write_camera_bank(CameraRefBank, pos + new Vector3(cp * sy, sp, cp * cy));
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

        if (ImGui.Checkbox("Follow attacker##fhparry.freecam.follow", ref _optionFollowCam))
            persist_settings();
        ImGui.SameLine();
        ImGui.TextDisabled("look-at eases onto the acting enemy (anchor or default eye), back when idle");
    }

    // Camera tab: the freecam controls on top, then a table of the anchors saved for the current
    // battle map. The current encounter is always a row (highlighted) even if it has no anchor yet,
    // so the status column shows at a glance whether this map+group is defined. Phase 1 lists only
    // what we have saved; a future pass can pre-populate every possible encounter from a joined
    // formations dataset (needs a runtime-scene to battle-id bridge we do not have yet).
    private void render_camera_tab()
    {
        render_freecam_panel();

        ImGui.SeparatorText("Encounters on this map");
        if (!try_get_live_battle_context(out _))
        {
            ImGui.TextDisabled("Not in battle.");
            return;
        }

        _getBattleScene ??= FhUtil.get_fptr<MsGetBattleSceneFn>(ExternalMemoryOffsetMap.Functions.MsGetBattleScene);
        int scene = _getBattleScene();
        string scenePrefix = $"{scene:X8}|";
        string currentKey = current_battle_camera_key();

        List<string> keys = [];
        foreach (string k in _cameraAnchors.Keys)
            if (k.StartsWith(scenePrefix, StringComparison.Ordinal)) keys.Add(k);
        if (!keys.Contains(currentKey)) keys.Add(currentKey);
        keys.Sort((a, b) => enemy_ids_from_key(b).Count.CompareTo(enemy_ids_from_key(a).Count));

        ImGui.TextDisabled($"map 0x{scene:X8} · {keys.Count} encounter(s) known here");

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##fhparry.cam.encounters", 3, flags))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 28f);
            ImGui.TableSetupColumn("Enemies", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Anchor", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < keys.Count; i++)
            {
                string k = keys[i];
                bool isCurrent = k == currentKey;
                bool hasAnchor = _cameraAnchors.ContainsKey(k);

                ImGui.TableNextRow();
                if (isCurrent)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.42f, 0.20f, 0.45f)));

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted((i + 1).ToString(CultureInfo.InvariantCulture));

                ImGui.TableSetColumnIndex(1);
                List<ushort> ids = enemy_ids_from_key(k);
                if (ids.Count == 0)
                {
                    ImGui.TextDisabled("(none)");
                }
                else
                {
                    for (int e = 0; e < ids.Count; e++)
                    {
                        if (e > 0) ImGui.SameLine();
                        string name = resolve_monster_name(ids[e]);
                        ImGui.TextUnformatted(truncate_display(name, 10));
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(name);
                    }
                }

                ImGui.TableSetColumnIndex(2);
                if (hasAnchor) ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.45f, 1f), "saved");
                else           ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.45f, 1f), "--");
            }
            ImGui.EndTable();
        }
    }

    private List<ushort> enemy_ids_from_key(string key)
    {
        List<ushort> ids = [];
        int bar = key.IndexOf('|');
        if (bar < 0 || bar + 1 >= key.Length) return ids;
        string cons = key[(bar + 1)..];
        if (cons == "none") return ids;
        foreach (string tok in cons.Split('-'))
            if (ushort.TryParse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id)) ids.Add(id);
        return ids;
    }

    private string resolve_monster_name(ushort id)
        => _dataMappings.TryResolveMonsterName(id, out string name) ? name : $"0x{id:X4}";

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
