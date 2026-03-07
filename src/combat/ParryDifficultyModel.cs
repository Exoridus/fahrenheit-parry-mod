namespace Fahrenheit.Mods.Parry;

public enum ParryDifficulty
{
    Easy = 0,
    Normal = 1,
    Expert = 2
}

public static class ParryDifficultyModel
{
    public const int MaxSpamTierIndex = 3;
    public const float SpamTierResetCooldownSeconds = 0.50f;

    private static readonly float[] EasyTierDurationsMs = [260f, 180f, 120f, 80f];
    private static readonly float[] NormalTierDurationsMs = [200f, 150f, 100f, 67f];
    private static readonly float[] ExpertTierDurationsMs = [150f, 75f, 50f, 33f];

    public static string FormatName(ParryDifficulty difficulty) => difficulty switch
    {
        ParryDifficulty.Easy => "Easy",
        ParryDifficulty.Expert => "Expert",
        _ => "Normal"
    };

    public static int ClampTierIndex(int tierIndex)
    {
        return Math.Clamp(tierIndex, 0, MaxSpamTierIndex);
    }

    public static float GetWindowSeconds(ParryDifficulty difficulty, int tierIndex)
    {
        ReadOnlySpan<float> tiers = get_tiers_ms(difficulty);
        int idx = ClampTierIndex(tierIndex);
        return tiers[idx] / 1000f;
    }

    public static float GetBaseWindowSeconds(ParryDifficulty difficulty)
    {
        return GetWindowSeconds(difficulty, tierIndex: 0);
    }

    public static int IncreaseSpamTier(int currentTierIndex)
    {
        return Math.Min(ClampTierIndex(currentTierIndex) + 1, MaxSpamTierIndex);
    }

    private static ReadOnlySpan<float> get_tiers_ms(ParryDifficulty difficulty) => difficulty switch
    {
        ParryDifficulty.Easy => EasyTierDurationsMs,
        ParryDifficulty.Expert => ExpertTierDurationsMs,
        _ => NormalTierDurationsMs
    };
}
