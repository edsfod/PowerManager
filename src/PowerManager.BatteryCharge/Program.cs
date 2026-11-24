using System.Globalization;
using PowerManager.Core.Abstractions;
using PowerManager.Core.Contracts;
using PowerManager.Core.Logic;
using PowerManager.Core.Models;
using PowerManager.Core.Policy;
using PowerManager.Hardware.MateBook;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.BatteryCharge;

public static class Program
{
    public static int Main(string[] args)
    {
        var paths = new AppPaths(AppPaths.ParseDataDirArg(args));
        bool statusOnly = HasFlag(args, "--status");
        bool dryRun = HasFlag(args, "--dry-run");
        string? cliReason = GetOption(args, "--reason");

        var adapter = new MateBookChargeHardwareAdapter();

        if (statusOnly)
        {
            return PrintStatus(paths, adapter);
        }

        BoostRequest? cliBoost = null;
        if (TryParseBoostArgs(args, out cliBoost, out string? parseError) == false)
        {
            Console.Error.WriteLine($"参数错误：{parseError}");
            return 1;
        }

        var ecLog = new RotatingUtcLogger(paths.EcLogPath);
        var historyLog = new RotatingUtcLogger(paths.HistoryLogPath);
        var errorLog = new RotatingUtcLogger(paths.BatteryErrorLogPath);

        try
        {
            paths.EnsureDirectoriesExist();
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

            IReadOnlyList<EcTaskRequest> queuedRequests = dryRun
                ? []
                : AtomicFileStore.ConsumeEcRequests(paths, msg => ecLog.Warn(msg));

            BoostRequest? effectiveBoostRequest = cliBoost
                ?? queuedRequests.LastOrDefault(r => r.Kind == EcTaskRequestKind.Boost)?.Boost;

            string runReason = cliReason
                ?? queuedRequests.LastOrDefault()?.Reason
                ?? (cliBoost is not null ? "CLI boost" : "scheduled");

            var prevState = AtomicFileStore.ReadJsonOrDefault<ChargeStateSnapshot>(paths.StateJsonPath);
            BoostState prevBoost = prevState?.Boost ?? BoostState.Inactive;

            var (acOpt, pctOpt) = PowerSchemeInterop.GetOsPowerStatus();
            bool acOnline = acOpt ?? false;
            int percent = pctOpt ?? 0;

            ChargeDecision decision = ChargeDecisionEngine.Decide(
                prevBoost,
                acOnline,
                percent,
                nowUtc,
                effectiveBoostRequest);

            if (dryRun)
            {
                WindowsBatteryTelemetry? winDry = TryReadWindowsBattery(adapter);
                Console.WriteLine($"[DRY-RUN] data_dir={paths.DataDir}");
                Console.WriteLine($"[DRY-RUN] os_power: ac={acOnline}, percent={percent}%");
                Console.WriteLine($"[DRY-RUN] decision: target={decision.Target.Start}%-{decision.Target.Stop}%, started={decision.Started}, reverted={decision.Reverted}, reason={decision.Reason}");
                if (winDry is not null)
                {
                    Console.WriteLine($"[DRY-RUN] win_battery: online={winDry.Online}, charging={winDry.Charging}, charge_mw={winDry.ChargeMw}, discharge_mw={winDry.DischargeMw}, rem={winDry.RemainingMwh}/{winDry.FullMwh} mWh");
                }
                return 0;
            }

            // 写入前先读 03 11，仅在不一致或读数异常时写 03 10，写后必须读回 03 11 校验
            ChargeThresholds? currentThresholds = adapter.ReadChargeThresholds();
            if (currentThresholds is null || currentThresholds != decision.Target)
            {
                string beforeText = currentThresholds is null ? "INVALID" : $"{currentThresholds.Start}-{currentThresholds.Stop}";
                try
                {
                    adapter.WriteChargeThresholdsAndVerify(decision.Target);
                    ecLog.Info($"WRITE {decision.Target.Start}-{decision.Target.Stop} (was {beforeText}) reason=\"{runReason}\" decision=\"{decision.Reason}\"");
                }
                catch (EcThresholdMismatchException ex)
                {
                    ecLog.Error($"MISMATCH {ex.Message} reason=\"{runReason}\"");
                    errorLog.Error(ex.ToString());
                    Console.Error.WriteLine(ex.Message);
                    return 2;
                }
            }

            EcTelemetry ecTelemetry = adapter.ReadEcTelemetry();
            WindowsBatteryTelemetry? winTelemetry = TryReadWindowsBattery(adapter);

            var snapshot = new ChargeStateSnapshot(
                UpdatedAtUtc: nowUtc,
                Ec: ecTelemetry,
                OsPower: new OsPowerSnapshot(acOnline, percent),
                Boost: decision.NextBoost,
                Target: [decision.Target.Start, decision.Target.Stop],
                Win: winTelemetry,
                RunReason: runReason);

            AtomicFileStore.WriteJsonAtomic(paths.StateJsonPath, snapshot);

            string tempText = ecTelemetry.BatteryC.HasValue ? $"{ecTelemetry.BatteryC.Value:0.0}C" : "N/A";
            string sysWText = ecTelemetry.SystemW.HasValue ? $"{ecTelemetry.SystemW.Value}W" : "N/A";
            string chgMaText = ecTelemetry.ChargeMa.HasValue ? $"{ecTelemetry.ChargeMa.Value}mA" : "N/A";
            historyLog.Info(
                $"ac={(acOnline ? 1 : 0)} pct={percent}% hw={ecTelemetry.HwStart}-{ecTelemetry.HwStop} " +
                $"target={decision.Target.Start}-{decision.Target.Stop} boost={(decision.NextBoost.Active ? 1 : 0)} " +
                $"bat={tempText} sys={sysWText} chg={chgMaText} reason=\"{runReason}\"");

            return 0;
        }
        catch (Exception ex)
        {
            errorLog.Error(ex.ToString());
            Console.Error.WriteLine($"[BatteryCharge] 失败：{ex.Message}");
            return 1;
        }
    }

    private static int PrintStatus(AppPaths paths, IChargeHardwareAdapter adapter)
    {
        var state = AtomicFileStore.ReadJsonOrDefault<ChargeStateSnapshot>(paths.StateJsonPath);
        var (acOnline, percent) = PowerSchemeInterop.GetOsPowerStatus();
        WindowsBatteryTelemetry? win = TryReadWindowsBattery(adapter);

        Console.WriteLine($"数据目录: {paths.DataDir}");
        Console.WriteLine($"系统供电: {(acOnline == true ? "插电 (AC)" : acOnline == false ? "电池 (DC)" : "未知")} | 电量: {(percent.HasValue ? $"{percent}%" : "未知")}");

        if (win is not null)
        {
            double chargeW = win.ChargeMw / 1000.0;
            double dischargeW = win.DischargeMw / 1000.0;
            Console.WriteLine($"电池状态: {(win.Charging ? "充电中" : "未充电")} | 充 {chargeW:F1} W / 放 {dischargeW:F1} W | 容量 {win.RemainingMwh}/{win.FullMwh} mWh");
        }

        if (state is null)
        {
            Console.WriteLine("EC 缓存: 尚无 state.json 记录（尚未运行过提权任务）。");
            return 0;
        }

        var localUpdated = state.UpdatedAtUtc.ToLocalTime();
        Console.WriteLine($"EC 阈值: {state.Ec.HwStart}%–{state.Ec.HwStop}% (目标 {state.Target[0]}%–{state.Target[1]}%, hw_ok={state.Ec.HwOk})");
        Console.WriteLine($"EC 读数: 电池 {(state.Ec.BatteryC.HasValue ? $"{state.Ec.BatteryC:F1} °C" : "无效/N/A")} | 整机 {(state.Ec.SystemW.HasValue ? $"{state.Ec.SystemW} W" : "N/A")} | 充电芯片 {(state.Ec.ChargeMa.HasValue ? $"{state.Ec.ChargeMa} mA" : "N/A")}");
        Console.WriteLine($"最近更新: {localUpdated:yyyy-MM-dd HH:mm:ss}（原因: {state.RunReason ?? "-"}）");

        if (state.Boost.Active)
        {
            string deadline = state.Boost.DeadlineUtc?.ToLocalTime().ToString("HH:mm") ?? "-";
            Console.WriteLine($"临时冲高: 进行中（目标 {state.Boost.TargetStart}%–{state.Boost.TargetStop}%，达标={state.Boost.ReachedTarget}，保持 {state.Boost.FullHoldMinutes} 分钟，最晚 {deadline}）");
        }
        else
        {
            Console.WriteLine($"临时冲高: 未激活{(string.IsNullOrWhiteSpace(state.Boost.LastReason) ? string.Empty : $"（上次：{state.Boost.LastReason}）")}");
        }

        return 0;
    }

    private static WindowsBatteryTelemetry? TryReadWindowsBattery(IChargeHardwareAdapter adapter)
    {
        try
        {
            return adapter.ReadWindowsBatteryTelemetry();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseBoostArgs(string[] args, out BoostRequest? boost, out string? error)
    {
        boost = null;
        error = null;

        string? boostVal = GetOption(args, "--boost");
        if (boostVal is null)
        {
            return true;
        }

        if (!int.TryParse(boostVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetStop))
        {
            error = $"无效的 --boost 目标值：{boostVal}";
            return false;
        }

        int holdMin = PowerPolicy.BoostHoldMinutesDefault;
        string? holdVal = GetOption(args, "--hold-min");
        if (holdVal is not null && !int.TryParse(holdVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out holdMin))
        {
            error = $"无效的 --hold-min 值：{holdVal}";
            return false;
        }

        double maxHours = PowerPolicy.BoostMaxHoursDefault;
        string? maxVal = GetOption(args, "--max-hours");
        if (maxVal is not null && !double.TryParse(maxVal, NumberStyles.Float, CultureInfo.InvariantCulture, out maxHours))
        {
            error = $"无效的 --max-hours 值：{maxVal}";
            return false;
        }

        var candidate = new BoostRequest(targetStop, holdMin, maxHours);
        if (!candidate.TryValidate(out error))
        {
            return false;
        }

        boost = candidate;
        return true;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            string prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i][prefix.Length..];
            }
        }

        return null;
    }
}
