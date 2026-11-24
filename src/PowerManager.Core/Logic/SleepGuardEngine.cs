using PowerManager.Core.Models;
using PowerManager.Core.Policy;

namespace PowerManager.Core.Logic;

public sealed record ForcedSleepState(
    DateTimeOffset StartedAtUtc,
    string TriggerNote,
    int RetryCount = 0);

public enum ForcedSleepTickAction
{
    ContinueWaiting,
    AbortDueToUserActivity,
    TriggerHibernate
}

public enum CommuteTempDecision
{
    IgnoreStale,
    IgnoreInvalidReading,
    Normal,
    SkipDueToActiveUser,
    TriggerOverheatSleep
}

/// <summary>
/// 合盖、通勤温控、防误伤与休眠兜底纯状态机（SLP-05 / SLP-06 / SLP-10 / SLP-11）。
/// </summary>
public static class SleepGuardEngine
{
    /// <summary>
    /// 普通模式合盖且无外接活动显示器时才触发 1 分钟延迟睡眠（SLP-06）。
    /// </summary>
    public static bool ShouldTriggerNormalLidCloseSleep(
        AgentMode mode,
        bool lidClosed,
        bool hasExternalActiveDisplay) =>
        mode == AgentMode.Normal && lidClosed && !hasExternalActiveDisplay;

    /// <summary>
    /// 主动睡眠期间每 1 分钟检查一次（SLP-10）：
    /// 开盖且 60 秒内有键鼠操作立即中止；持续未入睡满 2 分钟触发休眠兜底。
    /// </summary>
    public static ForcedSleepTickAction EvaluateForcedSleepTick(
        ForcedSleepState state,
        bool lidClosed,
        TimeSpan userIdle,
        DateTimeOffset nowUtc)
    {
        if (!lidClosed && userIdle < PowerPolicy.ForcedSleepActiveUserAbortIdle)
        {
            return ForcedSleepTickAction.AbortDueToUserActivity;
        }

        if ((nowUtc - state.StartedAtUtc) >= PowerPolicy.HibernateFallbackDelay)
        {
            return ForcedSleepTickAction.TriggerHibernate;
        }

        return ForcedSleepTickAction.ContinueWaiting;
    }

    /// <summary>
    /// SetSuspendState(Hibernate) 被系统拒绝时重置计时于下一轮重试（docs/design.md §二.2）。
    /// </summary>
    public static ForcedSleepState ResetAfterHibernateRejected(
        ForcedSleepState state,
        DateTimeOffset nowUtc) =>
        state with
        {
            StartedAtUtc = nowUtc,
            RetryCount = state.RetryCount + 1
        };

    /// <summary>
    /// 通勤电池温度检查（SLP-05）：
    /// state.json 过期 &gt; 120s 忽略；读数无效忽略；≥ 40 °C 时若开盖且 5 分钟内有键鼠操作跳过，否则提前触发睡眠。
    /// </summary>
    public static CommuteTempDecision EvaluateCommuteTemperature(
        double? batteryTempC,
        DateTimeOffset? stateUpdatedAtUtc,
        bool lidClosed,
        TimeSpan userIdle,
        DateTimeOffset nowUtc)
    {
        if (!stateUpdatedAtUtc.HasValue || (nowUtc - stateUpdatedAtUtc.Value) > PowerPolicy.CommuteTempStateMaxAge)
        {
            return CommuteTempDecision.IgnoreStale;
        }

        if (!batteryTempC.HasValue || double.IsNaN(batteryTempC.Value))
        {
            return CommuteTempDecision.IgnoreInvalidReading;
        }

        if (batteryTempC.Value < PowerPolicy.CommuteBatteryTempThresholdC)
        {
            return CommuteTempDecision.Normal;
        }

        if (!lidClosed && userIdle < PowerPolicy.CommuteTempActiveUserSkipIdle)
        {
            return CommuteTempDecision.SkipDueToActiveUser;
        }

        return CommuteTempDecision.TriggerOverheatSleep;
    }

    /// <summary>
    /// 保持运行电池通勤模式下，闲置达 32 分钟仍未睡下时由常驻接管主动睡眠（SLP-05）。
    /// </summary>
    public static bool ShouldTakeoverCommuteIdle(
        AgentMode mode,
        bool acOnline,
        TimeSpan userIdle,
        bool forcedSleepActive) =>
        mode == AgentMode.KeepRunning &&
        !acOnline &&
        !forcedSleepActive &&
        userIdle >= PowerPolicy.CommuteIdleTakeoverThreshold;

    /// <summary>
    /// 定时器 frozen 检测到唤醒且处于合盖状态，非“保持运行 + 插电”时启动 2 分钟重睡/休眠兜底（SLP-11）。
    /// </summary>
    public static bool ShouldTriggerUnexpectedWakeFallback(
        bool frozenDetected,
        bool lidClosed,
        AgentMode mode,
        bool acOnline) =>
        frozenDetected && lidClosed && !(mode == AgentMode.KeepRunning && acOnline);
}
