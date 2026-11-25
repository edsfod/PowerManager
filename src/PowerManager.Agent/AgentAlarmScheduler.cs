using PowerManager.Core.Logic;
using PowerManager.Core.Models;
using PowerManager.Core.Policy;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.Agent;

/// <summary>
/// 汇总所有定时候选点（模式到期、合盖睡眠、通勤查温、主动睡眠检查、冲高回落）并驱动单一 Waitable Timer。
/// </summary>
internal sealed class AgentAlarmScheduler
{
    private readonly AppPaths _paths;
    private readonly WaitableTimerHandle _timer;

    public AgentAlarmScheduler(AppPaths paths, WaitableTimerHandle timer)
    {
        _paths = paths;
        _timer = timer;
    }

    public bool IsTimerFrozen(DateTimeOffset nowUtc, out double driftSeconds)
    {
        if (_timer.ScheduledTargetUtc.HasValue &&
            WakeAlarmPlanner.IsTimerFrozen(_timer.ScheduledTargetUtc.Value, nowUtc))
        {
            driftSeconds = (nowUtc - _timer.ScheduledTargetUtc.Value).TotalSeconds;
            return true;
        }

        driftSeconds = 0;
        return false;
    }

    public bool IsBoostRevertDue(bool acOnline, DateTimeOffset nowUtc)
    {
        var state = AtomicFileStore.ReadJsonOrDefault<ChargeStateSnapshot>(_paths.StateJsonPath);
        if (state?.Boost.Active != true)
        {
            return false;
        }

        var b = state.Boost;
        return (b.DeadlineUtc.HasValue && nowUtc >= b.DeadlineUtc.Value)
            || (acOnline && b.FullSinceUtc.HasValue && nowUtc >= b.FullSinceUtc.Value.AddMinutes(b.FullHoldMinutes))
            || (!acOnline && !b.ReachedTarget && b.UnpluggedSinceUtc.HasValue && nowUtc >= b.UnpluggedSinceUtc.Value + PowerPolicy.BoostUnpluggedRevertDelay);
    }

    public void RecomputeAndArm(
        ModeState modeState,
        bool acOnline,
        DateTimeOffset? normalLidSleepDueUtc,
        DateTimeOffset? commuteTempCheckDueUtc,
        DateTimeOffset? commuteTempReadDueUtc,
        bool forcedSleepActive,
        DateTimeOffset nowUtc)
    {
        var candidates = new List<AlarmCandidate>();

        if (modeState.ParsedMode == AgentMode.KeepRunning && modeState.RunUntilUtc.HasValue)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.KeepRunningExpiry, modeState.RunUntilUtc.Value));
        }

        if (normalLidSleepDueUtc.HasValue)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.NormalLidCloseSleep, normalLidSleepDueUtc.Value));
        }

        if (commuteTempCheckDueUtc.HasValue)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.CommuteTempCheck, commuteTempCheckDueUtc.Value));
        }

        if (commuteTempReadDueUtc.HasValue)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.CommuteTempReadResult, commuteTempReadDueUtc.Value));
        }

        if (forcedSleepActive)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.ForcedSleepCheck, nowUtc + PowerPolicy.ForcedSleepCheckInterval));
        }

        var chargeState = AtomicFileStore.ReadJsonOrDefault<ChargeStateSnapshot>(_paths.StateJsonPath);
        if (chargeState?.Boost.Active == true)
        {
            var b = chargeState.Boost;
            if (b.DeadlineUtc.HasValue)
            {
                candidates.Add(new AlarmCandidate(AlarmKind.BoostDeadline, b.DeadlineUtc.Value));
            }
            if (acOnline && b.FullSinceUtc.HasValue)
            {
                candidates.Add(new AlarmCandidate(AlarmKind.BoostFullHoldComplete, b.FullSinceUtc.Value.AddMinutes(b.FullHoldMinutes)));
            }
            if (!acOnline && !b.ReachedTarget && b.UnpluggedSinceUtc.HasValue)
            {
                candidates.Add(new AlarmCandidate(AlarmKind.BoostUnpluggedRevert, b.UnpluggedSinceUtc.Value + PowerPolicy.BoostUnpluggedRevertDelay));
            }
        }

        var next = WakeAlarmPlanner.ComputeNextAlarm(candidates);
        if (next is null)
        {
            _timer.Cancel();
            return;
        }

        DateTimeOffset targetUtc = next.DueAtUtc <= nowUtc ? nowUtc.AddSeconds(1) : next.DueAtUtc;
        _timer.SetDueTimeUtc(targetUtc);
    }
}
