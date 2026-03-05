using Fahrenheit.Mods.Parry;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class ParryTimeSourceTests {
    [Fact]
    public void SimulationDeltaTimeSource_ShouldUseCapturedSimulationDelta() {
        var source = new SimulationDeltaTimeSource(1f / 30f);

        source.CaptureSimulationDelta(2f / 30f);
        Assert.Equal(2f / 30f, source.GetDeltaSeconds(), 4);

        source.CaptureSimulationDelta(4f / 30f);
        Assert.Equal(4f / 30f, source.GetDeltaSeconds(), 4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void SimulationDeltaTimeSource_ShouldFallbackForInvalidDelta(float invalidDelta) {
        var source = new SimulationDeltaTimeSource(1f / 30f);

        source.CaptureSimulationDelta(invalidDelta);

        Assert.Equal(1f / 30f, source.GetDeltaSeconds(), 4);
    }

    [Fact]
    public void SimulationDeltaTimeSource_ShouldClampLargeSpikes() {
        var source = new SimulationDeltaTimeSource(1f / 30f);

        source.CaptureSimulationDelta(1.0f);

        Assert.Equal(0.25f, source.GetDeltaSeconds(), 3);
    }

    [Fact]
    public void ParryWindowCountdown_ShouldBeDeterministicAcrossDifferentTickSizes() {
        float targetWindowSeconds = ParryDifficultyModel.GetWindowSeconds(ParryDifficulty.Normal, tierIndex: 0);
        Assert.Equal(0.200f, targetWindowSeconds, 3);

        float elapsed1x = simulate_countdown(targetWindowSeconds, 1f / 30f);
        float elapsed2x = simulate_countdown(targetWindowSeconds, 2f / 30f);
        float elapsed4x = simulate_countdown(targetWindowSeconds, 4f / 30f);

        Assert.InRange(elapsed1x, 0.20f, 0.24f);
        Assert.InRange(elapsed2x, 0.20f, 0.27f);
        Assert.InRange(elapsed4x, 0.20f, 0.34f);
    }

    [Fact]
    public void CalmReset_ShouldBeDeterministicAcrossVariableFramePacing() {
        var fixedStepController = new ParrySpamController();
        fixedStepController.ArmOnQualifyingRelease();
        fixedStepController.OnQualifyingPress();
        Assert.Equal(1, fixedStepController.TierIndex);

        var variableStepController = new ParrySpamController();
        variableStepController.ArmOnQualifyingRelease();
        variableStepController.OnQualifyingPress();
        Assert.Equal(1, variableStepController.TierIndex);

        for (int i = 0; i < 16; i++) {
            fixedStepController.Tick(1f / 30f);
        }

        variableStepController.Tick(0.05f);
        variableStepController.Tick(0.07f);
        variableStepController.Tick(0.09f);
        variableStepController.Tick(0.03f);
        variableStepController.Tick(0.11f);
        variableStepController.Tick(0.16f);

        Assert.Equal(0, fixedStepController.TierIndex);
        Assert.Equal(0, variableStepController.TierIndex);
    }

    private static float simulate_countdown(float initialSeconds, float deltaSeconds) {
        float remaining = initialSeconds;
        float elapsed = 0f;

        while (remaining > 0f) {
            remaining = MathF.Max(0f, remaining - deltaSeconds);
            elapsed += deltaSeconds;
        }

        return elapsed;
    }
}
