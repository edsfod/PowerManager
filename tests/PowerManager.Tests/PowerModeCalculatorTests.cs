using PowerManager.Core.Logic;
using PowerManager.Core.Models;
using PowerManager.Core.Policy;

namespace PowerManager.Tests;

public sealed class PowerModeCalculatorTests
{
    [Theory]
    [InlineData(true, 10, WindowsPowerMode.BestPerformance)]
    [InlineData(true, 100, WindowsPowerMode.BestPerformance)]
    [InlineData(false, 50, WindowsPowerMode.Balanced)]
    [InlineData(false, 80, WindowsPowerMode.Balanced)]
    [InlineData(false, 49, WindowsPowerMode.BestPowerEfficiency)]
    [InlineData(false, 5, WindowsPowerMode.BestPowerEfficiency)]
    public void GetWindowsPowerMode_FollowsPwr01AndPwr02(bool acOnline, int percent, WindowsPowerMode expected)
    {
        Assert.Equal(expected, PowerModeCalculator.GetWindowsPowerMode(acOnline, percent));
    }

    [Fact]
    public void GetTargetPowerScheme_NormalMode_MatchesSlp03AndSlp08()
    {
        var scheme = PowerModeCalculator.GetTargetPowerScheme(AgentMode.Normal, forcedSleepActive: false);
        Assert.Equal(900, scheme.AcDisplaySec);
        Assert.Equal(3600, scheme.AcSleepSec);
        Assert.Equal(300, scheme.DcDisplaySec);
        Assert.Equal(900, scheme.DcSleepSec);
        Assert.Equal(0, scheme.AcLidAction);
        Assert.Equal(0, scheme.DcLidAction);
        Assert.Equal(0, scheme.AcButtonAction);
        Assert.Equal(0, scheme.DcButtonAction);
    }

    [Fact]
    public void GetTargetPowerScheme_KeepRunningMode_MatchesSlp04AndSlp05()
    {
        var scheme = PowerModeCalculator.GetTargetPowerScheme(AgentMode.KeepRunning, forcedSleepActive: false);
        Assert.Equal(900, scheme.AcDisplaySec);
        Assert.Equal(0, scheme.AcSleepSec);
        Assert.Equal(300, scheme.DcDisplaySec);
        Assert.Equal(1800, scheme.DcSleepSec);
        Assert.Equal(0, scheme.AcLidAction);
        Assert.Equal(0, scheme.DcButtonAction);
    }

    [Fact]
    public void GetTargetPowerScheme_ForcedSleep_OverridesTo10sDisplay15sSleep()
    {
        var scheme = PowerModeCalculator.GetTargetPowerScheme(AgentMode.KeepRunning, forcedSleepActive: true);
        Assert.Equal(10, scheme.AcDisplaySec);
        Assert.Equal(15, scheme.AcSleepSec);
        Assert.Equal(10, scheme.DcDisplaySec);
        Assert.Equal(15, scheme.DcSleepSec);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(12, true)]
    [InlineData(24, true)]
    [InlineData(1, false)]
    [InlineData(6, false)]
    [InlineData(48, false)]
    public void IsValidKeepRunningDuration_ChecksAllowedOptions(int hours, bool expected)
    {
        Assert.Equal(expected, PowerModeCalculator.IsValidKeepRunningDuration(hours));
    }

    [Fact]
    public void EvaluateModeExpiry_RevertsToNormalAndFlagsNotificationAtDeadline()
    {
        var t0 = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var state = ModeState.CreateKeepRunning(t0, durationHours: 2);

        var (beforeState, beforeExpired) = PowerModeCalculator.EvaluateModeExpiry(state, t0.AddHours(2).AddSeconds(-1));
        Assert.False(beforeExpired);
        Assert.Equal(AgentMode.KeepRunning, beforeState.ParsedMode);

        var (atState, atExpired) = PowerModeCalculator.EvaluateModeExpiry(state, t0.AddHours(2));
        Assert.True(atExpired);
        Assert.Equal(AgentMode.Normal, atState.ParsedMode);
        Assert.Null(atState.RunUntilUtc);
    }

    [Theory]
    [InlineData(null, 80, false)]
    [InlineData(80, 80, false)]
    [InlineData(81, 82, false)]
    [InlineData(79, 80, true)]
    [InlineData(80, 79, true)]
    [InlineData(76, 75, true)]
    [InlineData(71, 68, true)]
    public void ShouldTriggerBatteryHistory_Detects5PercentSteps(int? prev, int curr, bool expected)
    {
        Assert.Equal(expected, PowerModeCalculator.ShouldTriggerBatteryHistory(prev, curr));
    }
}
