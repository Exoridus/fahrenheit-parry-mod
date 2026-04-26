using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Format contract for Stage-1 native-probe events. The formatter is the
///     single place hook bodies build their log line, so any consumer parsing
///     these lines (post-session analysis, KB ingestion) depends on this
///     shape staying stable.
/// </summary>
public sealed class Stage1ProbeFormatterTests
{
    [Fact]
    public void Format_With_Args_Includes_All_Required_Fields()
    {
        string line = Stage1ProbeFormatter.Format(
            probeName: "MsActionRequest",
            args: "target_id=2 attacker_id=10",
            frameIndex: 1234,
            inputState: ParryInputState.Open,
            currentAttackerId: 10,
            parryWindowActive: true);

        Assert.Equal(
            "[stage1.MsActionRequest] f=1234 target_id=2 attacker_id=10 state=Open atk=10 pwa=1",
            line);
    }

    [Fact]
    public void Format_With_Empty_Args_Omits_Args_Block_But_Keeps_Suffix()
    {
        string line = Stage1ProbeFormatter.Format(
            probeName: "MsCalcCommand",
            args: string.Empty,
            frameIndex: 7,
            inputState: ParryInputState.Ready,
            currentAttackerId: 0,
            parryWindowActive: false);

        Assert.Equal("[stage1.MsCalcCommand] f=7 state=Ready atk=0 pwa=0", line);
    }

    [Fact]
    public void Format_Encodes_Parry_Window_Active_As_Zero_One()
    {
        string off = Stage1ProbeFormatter.Format("X", "k=v", 1, ParryInputState.Ready, 0, false);
        string on  = Stage1ProbeFormatter.Format("X", "k=v", 1, ParryInputState.Open, 5, true);

        Assert.Contains(" pwa=0", off);
        Assert.Contains(" pwa=1", on);
    }

    [Fact]
    public void Format_Embeds_Probe_Name_With_Stage1_Prefix()
    {
        string line = Stage1ProbeFormatter.Format(
            "op_et_battle_genko_counter_get",
            "ret=0x0",
            42,
            ParryInputState.Resolved,
            7,
            false);

        Assert.StartsWith("[stage1.op_et_battle_genko_counter_get] ", line);
    }

    [Fact]
    public void FormatFailure_Is_Self_Describing()
    {
        string line = Stage1ProbeFormatter.FormatFailure("MsSetMotion", 99, "null deref in chr_id read");

        Assert.Equal(
            "[stage1.MsSetMotion] f=99 probe_fault reason=\"null deref in chr_id read\"",
            line);
    }
}
