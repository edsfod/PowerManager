using PowerManager.Core.Logic;
using PowerManager.Core.Models;

namespace PowerManager.Tests;

public sealed class ChargeDecisionEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Decide_WhenInactive_ReturnsDailyBaseline75To80()
    {
        var decision = ChargeDecisionEngine.Decide(BoostState.Inactive, acOnline: true, batteryPercent: 60, T0);
        Assert.Equal(new ChargeThresholds(75, 80), decision.Target);
        Assert.False(decision.Started);
        Assert.False(decision.Reverted);
    }

    [Fact]
    public void Decide_StartBoost_SetsTargetAndDeadline()
    {
        var req = new BoostRequest(TargetStop: 95, HoldMinutes: 60, MaxHours: 4.0);
        var decision = ChargeDecisionEngine.Decide(BoostState.Inactive, acOnline: true, batteryPercent: 78, T0, req);

        Assert.True(decision.Started);
        Assert.False(decision.Reverted);
        Assert.Equal(new ChargeThresholds(90, 95), decision.Target);
        Assert.True(decision.NextBoost.Active);
        Assert.False(decision.NextBoost.ReachedTarget);
        Assert.Null(decision.NextBoost.FullSinceUtc);
        Assert.Equal(T0.AddHours(4), decision.NextBoost.DeadlineUtc);
    }

    [Theory]
    [InlineData(84, 60, 4.0)]
    [InlineData(101, 60, 4.0)]
    [InlineData(90, 14, 4.0)]
    [InlineData(90, 181, 4.0)]
    [InlineData(90, 60, 0.5)]
    [InlineData(90, 60, 13.0)]
    public void Decide_StartBoost_RejectsOutOfRangeParameters(int targetStop, int holdMinutes, double maxHours)
    {
        var req = new BoostRequest(targetStop, holdMinutes, maxHours);
        Assert.Throws<ArgumentException>(() =>
            ChargeDecisionEngine.Decide(BoostState.Inactive, acOnline: true, batteryPercent: 70, T0, req));
    }

    [Fact]
    public void Decide_FullSinceIsContinuous_WhileReachedTargetIsIrreversible()
    {
        var req = new BoostRequest(TargetStop: 90, HoldMinutes: 60, MaxHours: 4.0);
        var started = ChargeDecisionEngine.Decide(BoostState.Inactive, acOnline: true, batteryPercent: 85, T0, req).NextBoost;

        // T+10m 到达 90%
        var atTarget = ChargeDecisionEngine.Decide(started, acOnline: true, batteryPercent: 90, T0.AddMinutes(10));
        Assert.True(atTarget.NextBoost.ReachedTarget);
        Assert.Equal(T0.AddMinutes(10), atTarget.NextBoost.FullSinceUtc);

        // T+40m 插电但跌破 90% -> full_since 清零，reached_target 保持 true（不可逆）
        var dipped = ChargeDecisionEngine.Decide(atTarget.NextBoost, acOnline: true, batteryPercent: 89, T0.AddMinutes(40));
        Assert.False(dipped.Reverted);
        Assert.True(dipped.NextBoost.ReachedTarget);
        Assert.Null(dipped.NextBoost.FullSinceUtc);

        // T+45m 重新回到 90% -> full_since 重新起算
        var backAtTarget = ChargeDecisionEngine.Decide(dipped.NextBoost, acOnline: true, batteryPercent: 90, T0.AddMinutes(45));
        Assert.Equal(T0.AddMinutes(45), backAtTarget.NextBoost.FullSinceUtc);

        // T+104m（仅连续保持 59 分钟）-> 尚未回落
        var hold59 = ChargeDecisionEngine.Decide(backAtTarget.NextBoost, acOnline: true, batteryPercent: 90, T0.AddMinutes(104));
        Assert.False(hold59.Reverted);

        // T+105m（连续保持满 60 分钟）-> 自动回落 75–80%
        var hold60 = ChargeDecisionEngine.Decide(hold59.NextBoost, acOnline: true, batteryPercent: 90, T0.AddMinutes(105));
        Assert.True(hold60.Reverted);
        Assert.Equal(ChargeThresholds.Default, hold60.Target);
        Assert.False(hold60.NextBoost.Active);
    }

    [Fact]
    public void Decide_UnpluggedAfterReachingTarget_RevertsImmediately()
    {
        var req = new BoostRequest(TargetStop: 90, HoldMinutes: 60, MaxHours: 4.0);
        var started = ChargeDecisionEngine.Decide(BoostState.Inactive, acOnline: true, batteryPercent: 90, T0, req).NextBoost;

        var unplugged = ChargeDecisionEngine.Decide(started, acOnline: false, batteryPercent: 89, T0.AddMinutes(5));
        Assert.True(unplugged.Reverted);
        Assert.Equal(ChargeThresholds.Default, unplugged.Target);
    }

    [Fact]
    public void Decide_UnpluggedBeforeTarget_RevertsOnlyAfter10Minutes()
    {
        var req = new BoostRequest(TargetStop: 95, HoldMinutes: 60, MaxHours: 4.0);
        var started = ChargeDecisionEngine.Decide(BoostState.Inactive, acOnline: true, batteryPercent: 80, T0, req).NextBoost;

        var u0 = ChargeDecisionEngine.Decide(started, acOnline: false, batteryPercent: 80, T0.AddMinutes(5));
        Assert.False(u0.Reverted);
        Assert.Equal(T0.AddMinutes(5), u0.NextBoost.UnpluggedSinceUtc);

        var u9 = ChargeDecisionEngine.Decide(u0.NextBoost, acOnline: false, batteryPercent: 79, T0.AddMinutes(14).AddSeconds(59));
        Assert.False(u9.Reverted);

        // 重新插电清零拔电计时
        var replugged = ChargeDecisionEngine.Decide(u9.NextBoost, acOnline: true, batteryPercent: 79, T0.AddMinutes(15));
        Assert.False(replugged.Reverted);
        Assert.Null(replugged.NextBoost.UnpluggedSinceUtc);

        // 再次拔电满 10 分钟后回落
        var uAgain = ChargeDecisionEngine.Decide(replugged.NextBoost, acOnline: false, batteryPercent: 79, T0.AddMinutes(20));
        var u10 = ChargeDecisionEngine.Decide(uAgain.NextBoost, acOnline: false, batteryPercent: 78, T0.AddMinutes(30));
        Assert.True(u10.Reverted);
        Assert.Equal(ChargeThresholds.Default, u10.Target);
    }

    [Fact]
    public void Decide_ExceededMaxDuration_RevertsToBaseline()
    {
        var req = new BoostRequest(TargetStop: 100, HoldMinutes: 60, MaxHours: 2.0);
        var started = ChargeDecisionEngine.Decide(BoostState.Inactive, acOnline: true, batteryPercent: 60, T0, req).NextBoost;

        var atDeadline = ChargeDecisionEngine.Decide(started, acOnline: true, batteryPercent: 92, T0.AddHours(2));
        Assert.True(atDeadline.Reverted);
        Assert.Equal(ChargeThresholds.Default, atDeadline.Target);
    }
}
