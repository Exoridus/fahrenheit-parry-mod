namespace Fahrenheit.Mods.Parry;

public readonly record struct ParrySpamTransition(
    bool TierChanged,
    int PreviousTier,
    int CurrentTier,
    bool Reset,
    string Reason
);

public sealed class ParrySpamController {
    private int _tierIndex;
    private bool _releaseArmed;
    private float _calmResetRemainingSeconds;

    public int TierIndex => ParryDifficultyModel.ClampTierIndex(_tierIndex);
    public bool ReleaseArmed => _releaseArmed;
    public float CalmResetRemainingSeconds => MathF.Max(0f, _calmResetRemainingSeconds);

    public void ArmOnQualifyingRelease() {
        _releaseArmed = true;
        mark_spam_activity();
    }

    public ParrySpamTransition OnQualifyingPress() {
        int before = TierIndex;
        if (_releaseArmed) {
            _tierIndex = ParryDifficultyModel.IncreaseSpamTier(_tierIndex);
            mark_spam_activity();
        }

        _releaseArmed = false;

        int after = TierIndex;
        return new ParrySpamTransition(
            TierChanged: before != after,
            PreviousTier: before,
            CurrentTier: after,
            Reset: false,
            Reason: string.Empty);
    }

    public ParrySpamTransition Tick(float deltaSeconds) {
        if (_calmResetRemainingSeconds <= 0f) {
            return default;
        }

        _calmResetRemainingSeconds = MathF.Max(0f, _calmResetRemainingSeconds - MathF.Max(0f, deltaSeconds));
        if (_calmResetRemainingSeconds > 0f) {
            return default;
        }

        return Reset("calm");
    }

    public ParrySpamTransition Reset(string reason) {
        int before = TierIndex;
        bool hadState = before != 0 || _releaseArmed || _calmResetRemainingSeconds > 0f;

        _tierIndex = 0;
        _releaseArmed = false;
        _calmResetRemainingSeconds = 0f;

        return new ParrySpamTransition(
            TierChanged: before != 0,
            PreviousTier: before,
            CurrentTier: 0,
            Reset: hadState,
            Reason: reason ?? string.Empty);
    }

    private void mark_spam_activity() {
        _calmResetRemainingSeconds = ParryDifficultyModel.SpamTierResetCooldownSeconds;
    }
}
