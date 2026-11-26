using PowerManager.Core.Models;
using PowerManager.Core.Policy;

namespace PowerManager.Core.Logic;

/// <summary>
/// 充电阈值与单次临时冲高纯决策引擎（CHG-01 / CHG-02 / ADR-0003）。
/// </summary>
public static class ChargeDecisionEngine
{
    public static ChargeDecision Decide(
        BoostState currentBoost,
        bool acOnline,
        int batteryPercent,
        DateTimeOffset nowUtc,
        BoostRequest? newBoostRequest = null)
    {
        if (newBoostRequest is not null)
        {
            if (!newBoostRequest.TryValidate(out var validationError))
            {
                throw new ArgumentException(validationError, nameof(newBoostRequest));
            }

            int targetStart = newBoostRequest.TargetStop - PowerPolicy.BoostHysteresisPercent;
            bool reached = batteryPercent >= newBoostRequest.TargetStop;
            DateTimeOffset? fullSince = acOnline && reached ? nowUtc : null;
            DateTimeOffset? unpluggedSince = acOnline ? null : nowUtc;
            DateTimeOffset deadline = nowUtc.AddHours(newBoostRequest.MaxHours);
            string startReason = $"Boost started: {targetStart}%-{newBoostRequest.TargetStop}% (hold {newBoostRequest.HoldMinutes}m, max {newBoostRequest.MaxHours:0.##}h)";

            var startedBoost = new BoostState(
                Active: true,
                TargetStart: targetStart,
                TargetStop: newBoostRequest.TargetStop,
                FullHoldMinutes: newBoostRequest.HoldMinutes,
                MaxTotalHours: newBoostRequest.MaxHours,
                StartedAtUtc: nowUtc,
                DeadlineUtc: deadline,
                FullSinceUtc: fullSince,
                ReachedTarget: reached,
                UnpluggedSinceUtc: unpluggedSince,
                RevertedAtUtc: null,
                LastReason: startReason);

            return new ChargeDecision(
                Target: new ChargeThresholds(targetStart, newBoostRequest.TargetStop),
                NextBoost: startedBoost,
                Started: true,
                Reverted: false,
                Reason: startReason);
        }

        if (!currentBoost.Active)
        {
            return new ChargeDecision(
                Target: ChargeThresholds.Default,
                NextBoost: currentBoost,
                Started: false,
                Reverted: false,
                Reason: $"Daily baseline {PowerPolicy.DefaultChargeStartPercent}%-{PowerPolicy.DefaultChargeStopPercent}%");
        }

        // reached_target 一旦达标即不可逆（docs/design.md“充电控制”）
        bool reachedTarget = currentBoost.ReachedTarget || batteryPercent >= currentBoost.TargetStop;

        // full_since 为连续达标插电计时：插电期间跌破目标或拔电即清零重计
        DateTimeOffset? nextFullSince = acOnline && batteryPercent >= currentBoost.TargetStop
            ? (currentBoost.FullSinceUtc ?? nowUtc)
            : null;

        // 未达标拔电持续计时：插电后清零
        DateTimeOffset? nextUnpluggedSince = !acOnline
            ? (currentBoost.UnpluggedSinceUtc ?? nowUtc)
            : null;

        string? revertReason = null;
        if (currentBoost.DeadlineUtc.HasValue && nowUtc >= currentBoost.DeadlineUtc.Value)
        {
            revertReason = $"Auto-reverted to {PowerPolicy.DefaultChargeStartPercent}%-{PowerPolicy.DefaultChargeStopPercent}%: Exceeded max duration ({currentBoost.MaxTotalHours:0.##}h)";
        }
        else if (reachedTarget && !acOnline)
        {
            revertReason = $"Auto-reverted to {PowerPolicy.DefaultChargeStartPercent}%-{PowerPolicy.DefaultChargeStopPercent}%: Unplugged after reaching {currentBoost.TargetStop}%";
        }
        else if (!reachedTarget && !acOnline && nextUnpluggedSince.HasValue &&
                 (nowUtc - nextUnpluggedSince.Value) >= PowerPolicy.BoostUnpluggedRevertDelay)
        {
            revertReason = $"Auto-reverted to {PowerPolicy.DefaultChargeStartPercent}%-{PowerPolicy.DefaultChargeStopPercent}%: Unplugged for >= {PowerPolicy.BoostUnpluggedRevertDelay.TotalMinutes:0} minutes before reaching target";
        }
        else if (acOnline && nextFullSince.HasValue &&
                 (nowUtc - nextFullSince.Value) >= TimeSpan.FromMinutes(currentBoost.FullHoldMinutes))
        {
            revertReason = $"Auto-reverted to {PowerPolicy.DefaultChargeStartPercent}%-{PowerPolicy.DefaultChargeStopPercent}%: Held >= {currentBoost.TargetStop}% on AC for {currentBoost.FullHoldMinutes} minutes";
        }

        if (revertReason is not null)
        {
            var revertedBoost = currentBoost with
            {
                Active = false,
                ReachedTarget = reachedTarget,
                FullSinceUtc = nextFullSince,
                UnpluggedSinceUtc = nextUnpluggedSince,
                RevertedAtUtc = nowUtc,
                LastReason = revertReason
            };

            return new ChargeDecision(
                Target: ChargeThresholds.Default,
                NextBoost: revertedBoost,
                Started: false,
                Reverted: true,
                Reason: revertReason);
        }

        var continuingBoost = currentBoost with
        {
            ReachedTarget = reachedTarget,
            FullSinceUtc = nextFullSince,
            UnpluggedSinceUtc = nextUnpluggedSince
        };

        return new ChargeDecision(
            Target: new ChargeThresholds(currentBoost.TargetStart, currentBoost.TargetStop),
            NextBoost: continuingBoost,
            Started: false,
            Reverted: false,
            Reason: $"Boost active: {currentBoost.TargetStart}%-{currentBoost.TargetStop}%");
    }
}
