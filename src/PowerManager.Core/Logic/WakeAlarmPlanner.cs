using PowerManager.Core.Policy;

namespace PowerManager.Core.Logic;

public enum AlarmKind
{
    KeepRunningExpiry,
    NormalLidCloseSleep,
    BoostFullHoldComplete,
    BoostDeadline,
    BoostUnpluggedRevert,
    CommuteTempCheck,
    CommuteTempReadResult,
    CommuteIdleTakeover,
    ForcedSleepCheck,
    UnexpectedWakeHibernate
}

public sealed record AlarmCandidate(AlarmKind Kind, DateTimeOffset DueAtUtc);

/// <summary>
/// 单一 Waitable Timer 调度与冻结唤醒（frozen）判定纯函数（ADR-0004 / SLP-11）。
/// </summary>
public static class WakeAlarmPlanner
{
    public static AlarmCandidate? ComputeNextAlarm(IEnumerable<AlarmCandidate> candidates)
    {
        AlarmCandidate? earliest = null;
        foreach (var candidate in candidates)
        {
            if (earliest is null || candidate.DueAtUtc < earliest.DueAtUtc)
            {
                earliest = candidate;
            }
        }

        return earliest;
    }

    public static TimeSpan ComputeWaitDuration(DateTimeOffset dueAtUtc, DateTimeOffset nowUtc)
    {
        var delta = dueAtUtc - nowUtc;
        return delta <= TimeSpan.Zero ? TimeSpan.Zero : delta;
    }

    /// <summary>
    /// 现代待机下通过 Waitable Timer 触发时刻晚于目标墙上时间严格 &gt; 30 秒判定刚经历冻结/睡眠（docs/design.md“单一定时器”）。
    /// </summary>
    public static bool IsTimerFrozen(DateTimeOffset scheduledTargetUtc, DateTimeOffset actualFiredUtc) =>
        (actualFiredUtc - scheduledTargetUtc) > PowerPolicy.TimerFrozenDriftThreshold;
}
