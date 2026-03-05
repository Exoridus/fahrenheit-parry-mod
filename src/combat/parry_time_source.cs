namespace Fahrenheit.Mods.Parry;

internal interface IParryTimeSource {
    float GetDeltaSeconds();
    void CaptureSimulationDelta(float deltaSeconds);
}

internal sealed class SimulationDeltaTimeSource(float fallbackDeltaSeconds) : IParryTimeSource {
    private readonly float _fallbackDeltaSeconds = sanitize_delta(fallbackDeltaSeconds, 1f / 30f);
    private float _latestDeltaSeconds = sanitize_delta(fallbackDeltaSeconds, 1f / 30f);

    public float GetDeltaSeconds() {
        return sanitize_delta(_latestDeltaSeconds, _fallbackDeltaSeconds);
    }

    public void CaptureSimulationDelta(float deltaSeconds) {
        _latestDeltaSeconds = sanitize_delta(deltaSeconds, _fallbackDeltaSeconds);
    }

    private static float sanitize_delta(float deltaSeconds, float fallbackSeconds) {
        if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds <= 0f) {
            return fallbackSeconds;
        }

        // Guard against bad spikes so timer transitions remain stable.
        return Math.Clamp(deltaSeconds, 0.001f, 0.25f);
    }
}
