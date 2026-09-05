namespace Fahrenheit.Mods.Parry;

/// <summary>
///     One decision for every battle-camera move, and a log line that says how it was reached.
///
///     <para>
///     Four classes of camera move reach us, and only two of them are established:
///     </para>
///     <list type="bullet">
///     <item><b>Flow</b> — the battle state machine's own framing, identified by a followed actor of
///     0xFF. MsBtlMain issues 0x18 at battle start and 0x75, 0x76 and 0x7c while the encounter
///     loads; state 0x1d picks 0x42 to 0x45 off btl.battle_end_type for the ending. None has a turn
///     to gate on, which is why they arrive with turn_active false and attacker 0.</item>
///     <item><b>Monster</b> — an enemy action's own camera script, while an enemy turn is active.</item>
///     <item><b>Character</b> — a party action's camera script. It runs with AwaitingTurnEnd false,
///     which is why the request lock alone never caught it and the writer stop exists.</item>
///     <item><b>Cutscene</b> — <i>not classified, and deliberately not guessed.</i> A mid-battle
///     event - a phase change, a transformation - writes through the same 0x6000 handlers as an
///     ability script and follows an actor just as one does, so neither the opcode nor the followed
///     actor separates them. The obvious signal, a live event id, does not exist: the only named
///     candidate, <c>g_curEventId</c> at <c>0x00ccb990</c>, is written once by
///     <c>SaveDataSetEventId</c> and has <b>zero readers</b> in the image - it records the last
///     event set, not whether one is running. What remains is the issuing
///     <c>AtelBasicWorker</c>, which arrives as the first argument of every camera handler and is
///     therefore certain rather than inferred. It is logged on every decision so the worker ids that
///     appear during a known cutscene can be read off a session log; until that is done there is no
///     cutscene gate, because a toggle that classifies on a guess is worse than no toggle.</item>
///     </list>
///
///     <para>
///     The log is the point. Every decision records what asked, which class it was put in, which
///     setting decided, who is attacking, whose turn it is and which worker issued it - so a wrong
///     classification is visible afterwards rather than inferred from a camera that behaved oddly.
///     </para>
/// </summary>
public unsafe sealed partial class ParryModule
{
    /// <summary>The engine's "no actor" sentinel, shared with Chr's attacker-id cluster.</summary>
    private const int SystemCameraActor = 0xFF;

    private enum CameraMoveClass
    {
        Flow,
        Monster,
        Character,
    }

    private long _cameraDecisions;
    private long _cameraAllowed;
    private long _cameraDenied;

    private static CameraMoveClass classify_camera_move(int followedActor, bool enemyTurnActive)
    {
        if (followedActor == SystemCameraActor) return CameraMoveClass.Flow;
        return enemyTurnActive ? CameraMoveClass.Monster : CameraMoveClass.Character;
    }

    /// <summary>
    ///     Whether this camera move may proceed. `followedActor` is the actor the camera script
    ///     follows where the call carries one, and -1 where it does not - a writer hook sees the
    ///     worker but not the request's actor, so it classifies on the turn alone.
    /// </summary>
    private bool camera_move_allowed(string source, int worker, int followedActor, string detail)
    {
        bool anyTurnActive   = _runtime.AwaitingTurnEnd;
        bool enemyTurnActive = anyTurnActive && _runtime.CurrentAttackerId >= PartyActorCapacity;

        CameraMoveClass moveClass = classify_camera_move(followedActor, enemyTurnActive);

        bool allowed = moveClass switch
        {
            CameraMoveClass.Flow      => _optionFlowCameraAllowed,
            CameraMoveClass.Monster   => _optionMonsterCameraAllowed,
            CameraMoveClass.Character => _optionCharacterCameraAllowed,
            _                         => true,
        };

        // The mod being off is not a permission decision - it means we are not in this at all.
        if (!_optionEnabled) allowed = true;

        _cameraDecisions++;
        if (allowed) _cameraAllowed++; else _cameraDenied++;

        if (_optionLogging || _optionCameraProbe)
        {
            log_debug(
                $"[Cam] {source} {(allowed ? "ALLOW" : "DENY")} class={moveClass} "
              + $"worker=0x{worker:X8} "
              + $"followed_actor={(followedActor < 0 ? "n/a" : $"0x{followedActor:X2}")} "
              + $"attacker=0x{_runtime.CurrentAttackerId:X2} turn_active={anyTurnActive} "
              + $"enemy_turn={enemyTurnActive} cam_id={_battleCameraId} "
              + $"settle={_cameraSettleSeconds:F2} freecam={_freecamActive} "
              + $"[{detail}] (allow={_cameraAllowed} deny={_cameraDenied})");
        }

        return allowed;
    }

    /// <summary>
    ///     Whether the camera should be held on a pose rather than merely left unmoved. Only the
    ///     Character axis does this: holding is what the freecam builds on, and it is also what makes
    ///     a denied move stay denied instead of the next writer creeping the camera along.
    ///
    ///     The settle grace stays: the game frames the battle at its correct default first, unless we
    ///     already have a pose to hold.
    /// </summary>
    private bool should_hold_camera_pose()
    {
        if (!_optionEnabled)                return false;
        if (_optionCharacterCameraAllowed)  return false;
        if (!try_get_live_battle_context(out _)) return false;
        if (_freecamActive)                 return true;
        return _cameraSettleSeconds == 0f;
    }
}
