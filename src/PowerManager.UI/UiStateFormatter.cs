using System.Text.RegularExpressions;
using PowerManager.Core.Models;
using PowerManager.Core.Policy;

namespace PowerManager.UI;

/// <summary>
/// 无 IO 副作用的 UI 视图拼装与中文展示文案格式化。
/// </summary>
internal static partial class UiStateFormatter
{
    private const int LowChargeMaThreshold = 1000;

    public static UiModeView BuildModeView(ModeState modeState, int prefHours, DateTimeOffset nowUtc)
    {
        int resolvedHours = PowerPolicy.KeepRunningDurationHoursOptions.Contains(prefHours)
            ? prefHours
            : PowerPolicy.KeepRunningDefaultHours;

        if (modeState.ParsedMode == AgentMode.KeepRunning &&
            modeState.RunUntilUtc.HasValue &&
            modeState.RunUntilUtc.Value > nowUtc)
        {
            var untilLocal = modeState.RunUntilUtc.Value.ToLocalTime();
            int totalSec = (int)(modeState.RunUntilUtc.Value - nowUtc).TotalSeconds;
            string left = totalSec >= 3600
                ? $"{totalSec / 3600} 小时 {(totalSec % 3600) / 60} 分"
                : $"{Math.Max(1, totalSec / 60)} 分钟";
            return new UiModeView("run", resolvedHours, untilLocal.ToString("HH:mm"), left, modeState.RunUntilUtc.Value.ToString("O"));
        }

        return new UiModeView("normal", resolvedHours, null, null, null);
    }

    public static UiBatteryView BuildBatteryView(bool acOnline, int? pct, ChargeStateSnapshot? state, DateTimeOffset nowUtc)
    {
        var ec = state?.Ec;
        var win = state?.Win;
        var boost = state?.Boost;

        int[] band = state?.Target is { Length: 2 } t
            ? t
            : [PowerPolicy.DefaultChargeStartPercent, PowerPolicy.DefaultChargeStopPercent];

        string flow = BuildFlowText(acOnline, pct, band[1], win);
        int[]? hw = ec is { HwOk: true, HwStart: not null, HwStop: not null }
            ? [ec.HwStart.Value, ec.HwStop.Value]
            : null;

        bool slow = acOnline
            && win is { Charging: true }
            && ec?.ChargeMa is not null
            && ec.ChargeMa.Value < LowChargeMaThreshold
            && pct is not null
            && pct.Value < band[1] - 1;

        string? readAt = state is not null
            ? state.UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm")
            : null;
        int? readAgeMinutes = state is not null
            ? Math.Max(0, (int)(nowUtc - state.UpdatedAtUtc).TotalMinutes)
            : null;

        string lastReason = boost is { Active: true }
            ? string.Empty
            : TranslateRevertReason(boost?.LastReason);

        return new UiBatteryView(
            Pct: pct,
            Ac: acOnline,
            Flow: flow,
            Band: band,
            Hw: hw,
            Temp: ec?.BatteryC,
            SystemW: ec?.SystemW,
            ChargeMa: ec?.ChargeMa,
            Hottest: ec?.HottestC,
            ReadAt: readAt,
            ReadAgeMinutes: readAgeMinutes,
            Slow: slow,
            Last: lastReason);
    }

    public static UiBoostView BuildBoostView(BoostState? boost, DateTimeOffset nowUtc)
    {
        if (boost is not { Active: true })
        {
            return new UiBoostView(Active: false);
        }

        int? heldMinutes = boost.FullSinceUtc.HasValue
            ? Math.Max(0, (int)(nowUtc - boost.FullSinceUtc.Value).TotalMinutes)
            : null;
        string? deadline = boost.DeadlineUtc.HasValue
            ? boost.DeadlineUtc.Value.ToLocalTime().ToString("HH:mm")
            : null;

        return new UiBoostView(
            Active: true,
            Target: boost.TargetStop,
            Hold: boost.FullHoldMinutes,
            Held: heldMinutes,
            FullSinceUtc: boost.FullSinceUtc?.ToString("O"),
            Deadline: deadline);
    }

    public static UiBoostPrefsView BuildBoostPrefsView(UiPrefs prefs) => new(
        Target: Math.Clamp(prefs.BoostTarget, PowerPolicy.BoostTargetMinPercent, PowerPolicy.BoostTargetMaxPercent),
        Hold: Math.Clamp(prefs.BoostHold, PowerPolicy.BoostHoldMinutesMin, PowerPolicy.BoostHoldMinutesMax),
        MaxHours: Math.Clamp(prefs.BoostMaxHours, PowerPolicy.BoostMaxHoursMin, PowerPolicy.BoostMaxHoursMax));

    public static string FormatPowerModeZh(WindowsPowerMode? mode) => mode switch
    {
        WindowsPowerMode.BestPowerEfficiency => "最佳能效",
        WindowsPowerMode.Balanced => "平衡",
        WindowsPowerMode.BestPerformance => "最佳性能",
        _ => "–"
    };

    public static string TranslateRevertReason(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var match = AutoRevertedRegex().Match(text);
        if (!match.Success)
        {
            return string.Empty;
        }

        string r = match.Groups[1].Value;
        r = HeldRegex().Replace(r, "充到 $1% 后插电保持满 $2 分钟");
        r = UnpluggedBeforeTargetRegex().Replace(r, "没充到就拔电满 $1 分钟");
        r = UnpluggedAfterTargetRegex().Replace(r, "充到 $1% 后拔了电");
        r = UnpluggedLegacyRegex().Replace(r, "充到后拔了电");
        r = MaxDurationRegex().Replace(r, "到了最长 $1 小时");
        return $"上次冲高已回落：{r}";
    }

    private static string BuildFlowText(bool acOnline, int? pct, int targetStop, WindowsBatteryTelemetry? win)
    {
        if (acOnline)
        {
            if (win is { ChargeMw: > 0 })
            {
                return $"接通电源 · 正在充电 {win.ChargeMw / 1000.0:F1} W";
            }

            if (pct.HasValue && pct.Value >= targetStop - 1)
            {
                return $"接通电源 · 已到 {targetStop}%，不再充";
            }

            return "接通电源 · 没在充电";
        }

        return win is { DischargeMw: > 0 }
            ? $"使用电池 · 放电 {win.DischargeMw / 1000.0:F1} W"
            : "使用电池";
    }

    [GeneratedRegex(@"^Auto-reverted to .*?: (.*)$")]
    private static partial Regex AutoRevertedRegex();

    [GeneratedRegex(@"Held >= (\d+)% on AC for (\d+) minutes")]
    private static partial Regex HeldRegex();

    [GeneratedRegex(@"Unplugged for (?:>= )?(\d+) minutes(?: before reaching target)?")]
    private static partial Regex UnpluggedBeforeTargetRegex();

    [GeneratedRegex(@"Unplugged after reaching (\d+)%")]
    private static partial Regex UnpluggedAfterTargetRegex();

    [GeneratedRegex(@"Unplugged on battery")]
    private static partial Regex UnpluggedLegacyRegex();

    [GeneratedRegex(@"(?:Exceeded max duration|Max boost duration) \(([\d.]+)h\)(?: elapsed)?")]
    private static partial Regex MaxDurationRegex();
}
