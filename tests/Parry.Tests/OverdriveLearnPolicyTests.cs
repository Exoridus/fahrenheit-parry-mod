using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Contract for the learn-by-parrying decision policy for the custom overdrive
///     mode (index 0x11 / bit 17). The policy is pure so the save-write decisions —
///     which are otherwise only exercised by writing into <c>save_ram</c> — are
///     unit-tested without a live memory read.
///
///     <para>
///         The load-bearing invariant under test: <c>counter[0x11]</c> must never
///         hold <c>0</c> while bit 17 is unset, because <c>MsLimitTypeProcess</c>
///         iterates <c>i &lt; 0x14</c> and would grant the mode incidentally. The
///         policy therefore never emits a bare decrement-to-zero: the last parry
///         grants (set bit, then write counter 0) as one decision.
///     </para>
/// </summary>
public sealed class OverdriveLearnPolicyTests
{
    private const short NotApplicable = unchecked((short)0xFFFF);

    [Fact]
    public void Init_NotApplicable_With_Bit_Unset_Initialises()
    {
        OverdriveLearnPolicy.InitDecision d = OverdriveLearnPolicy.DecideInitialisation(NotApplicable, modeBitSet: false);
        Assert.Equal(OverdriveLearnPolicy.InitAction.Initialise, d.Action);
        Assert.Equal(OverdriveLearnPolicy.LearnThreshold, d.WriteValue);
    }

    [Fact]
    public void Init_Zero_With_Bit_Set_Does_Nothing()
    {
        OverdriveLearnPolicy.InitDecision d = OverdriveLearnPolicy.DecideInitialisation(0, modeBitSet: true);
        Assert.Equal(OverdriveLearnPolicy.InitAction.NothingToDo, d.Action);
    }

    [Fact]
    public void Init_Zero_With_Bit_Unset_Initialises_And_Warns()
    {
        OverdriveLearnPolicy.InitDecision d = OverdriveLearnPolicy.DecideInitialisation(0, modeBitSet: false);
        Assert.Equal(OverdriveLearnPolicy.InitAction.InitialiseWithWarning, d.Action);
        Assert.Equal(OverdriveLearnPolicy.LearnThreshold, d.WriteValue);
    }

    [Fact]
    public void Init_Above_Threshold_Initialises()
    {
        OverdriveLearnPolicy.InitDecision d = OverdriveLearnPolicy.DecideInitialisation((short)(OverdriveLearnPolicy.LearnThreshold + 100), modeBitSet: false);
        Assert.Equal(OverdriveLearnPolicy.InitAction.Initialise, d.Action);
        Assert.Equal(OverdriveLearnPolicy.LearnThreshold, d.WriteValue);
    }

    [Fact]
    public void Init_In_Progress_Is_Left_Alone()
    {
        OverdriveLearnPolicy.InitDecision d = OverdriveLearnPolicy.DecideInitialisation(50, modeBitSet: false);
        Assert.Equal(OverdriveLearnPolicy.InitAction.LeaveInProgress, d.Action);
    }

    [Fact]
    public void Init_At_Threshold_Is_In_Progress()
    {
        OverdriveLearnPolicy.InitDecision d = OverdriveLearnPolicy.DecideInitialisation(OverdriveLearnPolicy.LearnThreshold, modeBitSet: false);
        Assert.Equal(OverdriveLearnPolicy.InitAction.LeaveInProgress, d.Action);
    }

    [Fact]
    public void Parry_On_One_Grants_And_Never_Bare_Decrements_To_Zero()
    {
        OverdriveLearnPolicy.ParryDecision d = OverdriveLearnPolicy.DecideParry(1, modeBitSet: false);
        Assert.Equal(OverdriveLearnPolicy.ParryAction.Grant, d.Action);
        Assert.True(d.SetBit);
        Assert.Equal((short)0, d.WriteCounterValue);
    }

    [Fact]
    public void Parry_On_Two_Decrements_To_One()
    {
        OverdriveLearnPolicy.ParryDecision d = OverdriveLearnPolicy.DecideParry(2, modeBitSet: false);
        Assert.Equal(OverdriveLearnPolicy.ParryAction.Decrement, d.Action);
        Assert.False(d.SetBit);
        Assert.Equal((short)1, d.WriteCounterValue);
    }

    [Fact]
    public void Parry_When_Bit_Already_Set_Is_NoOp()
    {
        OverdriveLearnPolicy.ParryDecision d = OverdriveLearnPolicy.DecideParry(0, modeBitSet: true);
        Assert.Equal(OverdriveLearnPolicy.ParryAction.AlreadyLearned, d.Action);
    }

    [Fact]
    public void Parry_On_Zero_With_Bit_Unset_Grants_To_Recover_Unsafe_State()
    {
        // counter 0 with bit unset is the unsafe state; a parry that lands here must
        // resolve it by granting (set bit, write 0) — never leave it decrementable.
        OverdriveLearnPolicy.ParryDecision d = OverdriveLearnPolicy.DecideParry(0, modeBitSet: false);
        Assert.Equal(OverdriveLearnPolicy.ParryAction.Grant, d.Action);
        Assert.True(d.SetBit);
        Assert.Equal((short)0, d.WriteCounterValue);
    }

    [Fact]
    public void Parry_On_NotApplicable_Does_Not_Learn()
    {
        OverdriveLearnPolicy.ParryDecision d = OverdriveLearnPolicy.DecideParry(NotApplicable, modeBitSet: false);
        Assert.Equal(OverdriveLearnPolicy.ParryAction.NotLearnable, d.Action);
    }
}
