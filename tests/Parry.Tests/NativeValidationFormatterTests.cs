using System.Text.Json;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class NativeValidationFormatterTests
{
    [Theory]
    [InlineData(null, true, false)]
    [InlineData("0", true, false)]
    [InlineData("true", true, false)]
    [InlineData("1", false, false)]
    [InlineData("1", true, true)]
    public void RequiresExplicitProcessOptInAndLogging(string? request, bool logging, bool expected)
        => Assert.Equal(expected, NativeValidationFormatter.IsEnabled(request, logging));

    [Fact]
    public void PreservesClockIdentityAndBothIndependentBankHeaders()
    {
        var actor = new NativeValidationActor(true, 120, 900, 1, 0, 1, 0x200, 3, 0, 2,
            new(1, 4, 22, 2), new(0, 3, 23, 5));
        var record = new NativeValidationRecord(7, 123456789, 10000000, 19, 101, 6, 4,
            "commit", "pre", "pending", 0, 22, 2, -1, 2, -1, 1024, -1, 8, 99, 105, 105, true,
            ParryInputState.Resolved, 22, 22, 1, 90, 123, 4, 4, 4, 0, 0, actor);
        string line = NativeValidationFormatter.Format(record);
        using var json = JsonDocument.Parse(line["[native-validation] ".Length..]);
        var root = json.RootElement;
        Assert.Equal(101ul, root.GetProperty("simulation_tick").GetUInt64());
        Assert.Equal(123456789, root.GetProperty("qpc").GetInt64());
        Assert.Equal(8, root.GetProperty("window_generation").GetInt64());
        Assert.Equal(105ul, root.GetProperty("window_deadline_tick").GetUInt64());
        Assert.Equal(-1, root.GetProperty("batch_hit_count").GetInt32());
        Assert.Equal(1024, root.GetProperty("phase_flags").GetInt32());
        Assert.Equal(-1, root.GetProperty("command_id").GetInt32());
        Assert.Equal(2, root.GetProperty("command_index").GetInt32());
        Assert.Equal(-1, root.GetProperty("motion_mode").GetInt32());
        Assert.Equal(22, root.GetProperty("cue_attacker").GetInt32());
        Assert.Equal(22, root.GetProperty("armed_attacker").GetInt32());
        var state = root.GetProperty("actor");
        Assert.Equal(120, state.GetProperty("hp").GetInt32());
        Assert.Equal(900, state.GetProperty("damage_hp").GetInt32());
        Assert.Equal(1, state.GetProperty("death_latch").GetInt32());
        Assert.Equal(0x200u, state.GetProperty("status").GetUInt32());
        Assert.Equal(1, state.GetProperty("bank0").GetProperty("cursor").GetInt32());
        Assert.Equal(4, state.GetProperty("bank0").GetProperty("count").GetInt32());
        Assert.Equal(22, state.GetProperty("bank0").GetProperty("attacker").GetInt32());
        Assert.Equal(2, state.GetProperty("bank0").GetProperty("command_index").GetInt32());
        Assert.Equal(0, state.GetProperty("bank1").GetProperty("cursor").GetInt32());
        Assert.Equal(3, state.GetProperty("bank1").GetProperty("count").GetInt32());
        Assert.Equal(23, state.GetProperty("bank1").GetProperty("attacker").GetInt32());
        Assert.Equal(5, state.GetProperty("bank1").GetProperty("command_index").GetInt32());
    }
}
