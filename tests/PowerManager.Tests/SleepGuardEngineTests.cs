using PowerManager.Core.Logic;
using PowerManager.Core.Models;

namespace PowerManager.Tests;

public sealed class SleepGuardEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentMode.Normal, true, false, true)]
    [InlineData(AgentMode.Normal, true, true, false)]
    [InlineData(AgentMode.Normal, false, false, false)]
    [InlineData(AgentMode.KeepRunning, true, false, false)]
    public void ShouldTriggerNormalLidCloseSleep_ChecksModeLidAndExternalDisplay(
        AgentMode mode, bool lidClosed, bool hasExt, bool expected)
    {
        Assert.Equal(expected, SleepGuardEngine.ShouldTriggerNormalLidCloseSleep(mode, lidClosed, hasExt));
    }

    [Fact]
    public void EvaluateForcedSleepTick_AbortsWhenLidOpenAndUserActiveWithin60s()
    {
        var state = new ForcedSleepState(T0, "合盖 1 分钟");

        // 开盖且 59 秒内有操作 -> 立即中止
        Assert.Equal(
            ForcedSleepTickAction.AbortDueToUserActivity,
            SleepGuardEngine.EvaluateForcedSleepTick(state, lidClosed: false, TimeSpan.FromSeconds(59), T0.AddMinutes(1)));

        // 开盖但已闲置 60 秒 -> 不中止，继续等待
        Assert.Equal(
            ForcedSleepTickAction.ContinueWaiting,
            SleepGuardEngine.EvaluateForcedSleepTick(state, lidClosed: false, TimeSpan.FromSeconds(60), T0.AddMinutes(1)));

        // 合盖即使 idle 很短 -> 不中止，满 2 分钟触发休眠
        Assert.Equal(
            ForcedSleepTickAction.TriggerHibernate,
            SleepGuardEngine.EvaluateForcedSleepTick(state, lidClosed: true, TimeSpan.FromSeconds(5), T0.AddMinutes(2)));
    }

    [Fact]
    public void ResetAfterHibernateRejected_ResetsTimerAndIncrementsRetryCount()
    {
        var state = new ForcedSleepState(T0, "过热 40 °C");
        var reset = SleepGuardEngine.ResetAfterHibernateRejected(state, T0.AddMinutes(2));

        Assert.Equal(T0.AddMinutes(2), reset.StartedAtUtc);
        Assert.Equal(1, reset.RetryCount);
        Assert.Equal(
            ForcedSleepTickAction.ContinueWaiting,
            SleepGuardEngine.EvaluateForcedSleepTick(reset, lidClosed: true, TimeSpan.FromMinutes(1), T0.AddMinutes(3)));
    }

    [Fact]
    public void EvaluateCommuteTemperature_EnforcesStalenessInvalidAndActiveUserSkip()
    {
        // state.json 过期 > 120s 忽略
        Assert.Equal(
            CommuteTempDecision.IgnoreStale,
            SleepGuardEngine.EvaluateCommuteTemperature(42.0, T0.AddSeconds(-121), lidClosed: true, TimeSpan.FromMinutes(10), T0));

        // 原始值为 0 解析出的 null -> 忽略
        Assert.Equal(
            CommuteTempDecision.IgnoreInvalidReading,
            SleepGuardEngine.EvaluateCommuteTemperature(null, T0.AddSeconds(-10), lidClosed: true, TimeSpan.FromMinutes(10), T0));

        // 低于 40 °C -> 正常
        Assert.Equal(
            CommuteTempDecision.Normal,
            SleepGuardEngine.EvaluateCommuteTemperature(39.9, T0.AddSeconds(-10), lidClosed: true, TimeSpan.FromMinutes(10), T0));

        // >= 40 °C 但开盖且 5 分钟内有键鼠操作 -> 跳过防误伤
        Assert.Equal(
            CommuteTempDecision.SkipDueToActiveUser,
            SleepGuardEngine.EvaluateCommuteTemperature(40.0, T0.AddSeconds(-10), lidClosed: false, TimeSpan.FromMinutes(4.9), T0));

        // >= 40 °C 且合盖（或开盖已闲置 >= 5 分钟）-> 提前触发睡眠
        Assert.Equal(
            CommuteTempDecision.TriggerOverheatSleep,
            SleepGuardEngine.EvaluateCommuteTemperature(40.0, T0.AddSeconds(-10), lidClosed: true, TimeSpan.FromSeconds(30), T0));
        Assert.Equal(
            CommuteTempDecision.TriggerOverheatSleep,
            SleepGuardEngine.EvaluateCommuteTemperature(40.5, T0.AddSeconds(-10), lidClosed: false, TimeSpan.FromMinutes(5), T0));
    }

    [Theory]
    [InlineData(AgentMode.KeepRunning, false, 32, false, true)]
    [InlineData(AgentMode.KeepRunning, false, 31, false, false)]
    [InlineData(AgentMode.KeepRunning, true, 40, false, false)]
    [InlineData(AgentMode.Normal, false, 40, false, false)]
    [InlineData(AgentMode.KeepRunning, false, 40, true, false)]
    public void ShouldTakeoverCommuteIdle_TriggersOnlyAt32MinOnBatteryInKeepRunning(
        AgentMode mode, bool acOnline, int idleMinutes, bool forcedSleepActive, bool expected)
    {
        Assert.Equal(
            expected,
            SleepGuardEngine.ShouldTakeoverCommuteIdle(mode, acOnline, TimeSpan.FromMinutes(idleMinutes), forcedSleepActive));
    }

    [Theory]
    [InlineData(true, true, AgentMode.Normal, true, true)]
    [InlineData(true, true, AgentMode.Normal, false, true)]
    [InlineData(true, true, AgentMode.KeepRunning, false, true)]
    [InlineData(true, true, AgentMode.KeepRunning, true, false)]
    [InlineData(false, true, AgentMode.Normal, false, false)]
    [InlineData(true, false, AgentMode.Normal, false, false)]
    public void ShouldTriggerUnexpectedWakeFallback_FollowsSlp11(
        bool frozen, bool lidClosed, AgentMode mode, bool acOnline, bool expected)
    {
        Assert.Equal(expected, SleepGuardEngine.ShouldTriggerUnexpectedWakeFallback(frozen, lidClosed, mode, acOnline));
    }
}
