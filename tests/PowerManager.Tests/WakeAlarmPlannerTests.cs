using PowerManager.Core.Logic;

namespace PowerManager.Tests;

public sealed class WakeAlarmPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComputeNextAlarm_ReturnsEarliestCandidateOrNull()
    {
        Assert.Null(WakeAlarmPlanner.ComputeNextAlarm([]));

        var candidates = new[]
        {
            new AlarmCandidate(AlarmKind.BoostDeadline, T0.AddHours(4)),
            new AlarmCandidate(AlarmKind.CommuteTempCheck, T0.AddMinutes(3)),
            new AlarmCandidate(AlarmKind.NormalLidCloseSleep, T0.AddMinutes(1))
        };

        var next = WakeAlarmPlanner.ComputeNextAlarm(candidates);
        Assert.NotNull(next);
        Assert.Equal(AlarmKind.NormalLidCloseSleep, next.Kind);
        Assert.Equal(T0.AddMinutes(1), next.DueAtUtc);
    }

    [Fact]
    public void ComputeWaitDuration_ClampsNegativeToZero()
    {
        Assert.Equal(TimeSpan.Zero, WakeAlarmPlanner.ComputeWaitDuration(T0.AddSeconds(-5), T0));
        Assert.Equal(TimeSpan.FromSeconds(45), WakeAlarmPlanner.ComputeWaitDuration(T0.AddSeconds(45), T0));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(30.0, false)]
    [InlineData(30.001, true)]
    [InlineData(120.0, true)]
    [InlineData(-1.0, false)]
    public void IsTimerFrozen_StrictlyGreaterThan30Seconds(double driftSeconds, bool expectedFrozen)
    {
        var fired = T0.AddSeconds(driftSeconds);
        Assert.Equal(expectedFrozen, WakeAlarmPlanner.IsTimerFrozen(T0, fired));
    }
}
