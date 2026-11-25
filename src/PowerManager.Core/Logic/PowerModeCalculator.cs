using PowerManager.Core.Models;
using PowerManager.Core.Policy;

namespace PowerManager.Core.Logic;

public static class PowerModeCalculator
{
    /// <summary>
    /// 计算目标 Windows 电源模式（PWR-01 / PWR-02）。
    /// </summary>
    public static WindowsPowerMode GetWindowsPowerMode(bool acOnline, int batteryPercent)
    {
        if (acOnline)
        {
            return WindowsPowerMode.BestPerformance;
        }

        return batteryPercent >= PowerPolicy.DcBalancedMinPercent
            ? WindowsPowerMode.Balanced
            : WindowsPowerMode.BestPowerEfficiency;
    }

    /// <summary>
    /// 计算当前模式（含主动诱导睡眠覆盖）对应的 Windows 关屏/睡眠超时与按键动作（SLP-03..06, SLP-08）。
    /// </summary>
    public static PowerSchemeSettings GetTargetPowerScheme(AgentMode mode, bool forcedSleepActive)
    {
        if (forcedSleepActive)
        {
            return new PowerSchemeSettings(
                AcDisplaySec: PowerPolicy.ForcedSleepDisplayTimeoutSec,
                AcSleepSec: PowerPolicy.ForcedSleepTimeoutSec,
                DcDisplaySec: PowerPolicy.ForcedSleepDisplayTimeoutSec,
                DcSleepSec: PowerPolicy.ForcedSleepTimeoutSec,
                AcLidAction: PowerPolicy.DoNothingPowerAction,
                DcLidAction: PowerPolicy.DoNothingPowerAction,
                AcButtonAction: PowerPolicy.DoNothingPowerAction,
                DcButtonAction: PowerPolicy.DoNothingPowerAction);
        }

        return mode == AgentMode.KeepRunning
            ? new PowerSchemeSettings(
                AcDisplaySec: PowerPolicy.KeepRunningAcDisplayTimeoutSec,
                AcSleepSec: PowerPolicy.KeepRunningAcSleepTimeoutSec,
                DcDisplaySec: PowerPolicy.KeepRunningDcDisplayTimeoutSec,
                DcSleepSec: PowerPolicy.KeepRunningDcSleepTimeoutSec,
                AcLidAction: PowerPolicy.DoNothingPowerAction,
                DcLidAction: PowerPolicy.DoNothingPowerAction,
                AcButtonAction: PowerPolicy.DoNothingPowerAction,
                DcButtonAction: PowerPolicy.DoNothingPowerAction)
            : new PowerSchemeSettings(
                AcDisplaySec: PowerPolicy.NormalAcDisplayTimeoutSec,
                AcSleepSec: PowerPolicy.NormalAcSleepTimeoutSec,
                DcDisplaySec: PowerPolicy.NormalDcDisplayTimeoutSec,
                DcSleepSec: PowerPolicy.NormalDcSleepTimeoutSec,
                AcLidAction: PowerPolicy.DoNothingPowerAction,
                DcLidAction: PowerPolicy.DoNothingPowerAction,
                AcButtonAction: PowerPolicy.DoNothingPowerAction,
                DcButtonAction: PowerPolicy.DoNothingPowerAction);
    }

    public static bool IsValidKeepRunningDuration(int hours) =>
        hours == PowerPolicy.KeepRunningIndefiniteHours
        || (hours >= PowerPolicy.KeepRunningMinHours && hours <= PowerPolicy.KeepRunningMaxHours);

    /// <summary>
    /// 评估保持运行模式是否到期（SLP-02）。到期时返回回落后的普通模式与通知标记。
    /// </summary>
    public static (ModeState NextState, bool ExpiredToNormal) EvaluateModeExpiry(
        ModeState current,
        DateTimeOffset nowUtc)
    {
        if (current.ParsedMode != AgentMode.KeepRunning)
        {
            return (current, false);
        }

        if (current.RunUntilUtc.HasValue && nowUtc >= current.RunUntilUtc.Value)
        {
            return (ModeState.CreateNormal(nowUtc), true);
        }

        return (current, false);
    }

    /// <summary>
    /// 判断电量变化是否触发 5% 步进历史记录（CHG-03）。
    /// </summary>
    public static bool ShouldTriggerBatteryHistory(int? previousPercent, int currentPercent)
    {
        if (!previousPercent.HasValue || previousPercent.Value == currentPercent)
        {
            return false;
        }

        int prevBucket = previousPercent.Value / PowerPolicy.BatteryHistoryStepPercent;
        int currBucket = currentPercent / PowerPolicy.BatteryHistoryStepPercent;
        return prevBucket != currBucket || currentPercent % PowerPolicy.BatteryHistoryStepPercent == 0;
    }
}
