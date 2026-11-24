using System.Globalization;
using PowerManager.Core.Contracts;
using PowerManager.Core.Logic;
using PowerManager.Core.Models;
using PowerManager.Core.Policy;
using PowerManager.Platform.Ipc;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || HasFlag(args, "-h") || HasFlag(args, "--help"))
        {
            PrintUsage();
            return 0;
        }

        var paths = new AppPaths(AppPaths.ParseDataDirArg(args));
        string pipeName = GetOption(args, "--pipe-name") ?? IpcConstants.AgentPipeName;
        bool dryRun = HasFlag(args, "--dry-run");
        string command = args[0].ToLowerInvariant();

        return command switch
        {
            "status" => await RunStatusAsync(paths, pipeName),
            "mode" => await RunModeAsync(args, pipeName),
            "restore-original" => await RunRestoreOriginalAsync(paths, pipeName, dryRun),
            "boost" => RunBoost(args, paths, dryRun),
            "paths" => PrintPaths(paths),
            _ => UnknownCommand(command)
        };
    }

    private static async Task<int> RunStatusAsync(AppPaths paths, string pipeName)
    {
        Console.WriteLine($"数据目录: {paths.DataDir}");

        AgentIpcResponse? agentResp = null;
        try
        {
            agentResp = await AgentNamedPipeClient.SendAsync(new AgentIpcRequest(AgentIpcCommand.GetStatus), pipeName, timeoutMs: 1200);
        }
        catch
        {
            // Agent 未运行或管道不可达
        }

        if (agentResp is { Ok: true })
        {
            string modeLabel = agentResp.ModeState?.ParsedMode == AgentMode.KeepRunning
                ? $"保持运行（至 {agentResp.ModeState.RunUntilUtc?.ToLocalTime():yyyy-MM-dd HH:mm}）"
                : "普通模式";
            Console.WriteLine($"常驻调度: 运行中 | 模式: {modeLabel} | 主动睡眠: {(agentResp.ForcedSleepActive ? "是" : "否")}");
        }
        else
        {
            var modeFile = AtomicFileStore.ReadJsonOrDefault<ModeState>(paths.ModeJsonPath);
            string cached = modeFile is null ? "未知" : (modeFile.ParsedMode == AgentMode.KeepRunning ? "保持运行（缓存）" : "普通模式（缓存）");
            Console.WriteLine($"常驻调度: 未运行（管道 {pipeName} 不可达）| 缓存模式: {cached}");
        }

        var (acOnline, percent) = PowerSchemeInterop.GetOsPowerStatus();
        var acMode = PowerSchemeInterop.GetUserPowerMode(ac: true);
        var dcMode = PowerSchemeInterop.GetUserPowerMode(ac: false);
        var scheme = PowerSchemeInterop.ReadCurrentSettings();
        bool extDisplay = DisplayAndInputInterop.HasExternalActiveDisplay();

        Console.WriteLine($"供电状态: {(acOnline == true ? "插电 (AC)" : acOnline == false ? "电池 (DC)" : "未知")} | 电量: {(percent.HasValue ? $"{percent}%" : "未知")} | 外接活动屏: {(extDisplay ? "是" : "否")}");
        Console.WriteLine($"电源模式: AC={acMode?.ToString() ?? "未知"} | DC={dcMode?.ToString() ?? "未知"}");
        Console.WriteLine($"当前超时: AC 关屏 {scheme.AcDisplaySec}s / 睡眠 {scheme.AcSleepSec}s | DC 关屏 {scheme.DcDisplaySec}s / 睡眠 {scheme.DcSleepSec}s | 合盖={scheme.AcLidAction}/{scheme.DcLidAction} | 电源键={scheme.AcButtonAction}/{scheme.DcButtonAction}");

        var state = AtomicFileStore.ReadJsonOrDefault<ChargeStateSnapshot>(paths.StateJsonPath);
        if (state is null)
        {
            Console.WriteLine("充电缓存: 尚无 state.json 记录。");
        }
        else
        {
            Console.WriteLine($"EC 阈值: {state.Ec.HwStart}%–{state.Ec.HwStop}%（目标 {state.Target[0]}%–{state.Target[1]}%，更新于 {state.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}，原因: {state.RunReason ?? "-"}）");
            Console.WriteLine($"EC 遥测: 电池 {(state.Ec.BatteryC.HasValue ? $"{state.Ec.BatteryC:F1} °C" : "无效/N/A")} | 整机 {(state.Ec.SystemW.HasValue ? $"{state.Ec.SystemW} W" : "N/A")} | 充电电流 {(state.Ec.ChargeMa.HasValue ? $"{state.Ec.ChargeMa} mA" : "N/A")}");
            if (state.Boost.Active)
            {
                Console.WriteLine($"临时冲高: 进行中（目标 {state.Boost.TargetStart}%–{state.Boost.TargetStop}%，达标={state.Boost.ReachedTarget}，保持 {state.Boost.FullHoldMinutes}m，最晚 {state.Boost.DeadlineUtc?.ToLocalTime():HH:mm}）");
            }
            else
            {
                Console.WriteLine("临时冲高: 未激活");
            }
        }

        return 0;
    }

    private static async Task<int> RunModeAsync(string[] args, string pipeName)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: pmctl mode <normal|keep-running> [--hours 2|4|8|12|24]");
            return 1;
        }

        string modeArg = args[1].ToLowerInvariant();
        AgentMode targetMode;
        int? durationHours = null;

        if (modeArg == "normal")
        {
            targetMode = AgentMode.Normal;
        }
        else if (modeArg is "keep-running" or "keep_running")
        {
            targetMode = AgentMode.KeepRunning;
            string? hoursText = GetOption(args, "--hours");
            durationHours = hoursText is null
                ? PowerPolicy.KeepRunningDefaultHours
                : int.TryParse(hoursText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ? h : -1;

            if (!PowerModeCalculator.IsValidKeepRunningDuration(durationHours.Value))
            {
                Console.Error.WriteLine("无效时长：--hours 仅支持 2 / 4 / 8 / 12 / 24。");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine($"未知模式：{args[1]}（可选 normal 或 keep-running）。");
            return 1;
        }

        try
        {
            var resp = await AgentNamedPipeClient.SendAsync(
                new AgentIpcRequest(AgentIpcCommand.SetMode, targetMode, durationHours),
                pipeName);
            Console.WriteLine(resp.Message ?? (resp.Ok ? "OK" : "Failed"));
            return resp.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"无法连接 Agent（{pipeName}）：{ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunRestoreOriginalAsync(AppPaths paths, string pipeName, bool dryRun)
    {
        try
        {
            var resp = await AgentNamedPipeClient.SendAsync(new AgentIpcRequest(AgentIpcCommand.RestoreOriginal), pipeName, timeoutMs: 2000);
            Console.WriteLine(resp.Message ?? "已通知 Agent 恢复并退出。");
            return resp.Ok ? 0 : 1;
        }
        catch
        {
            // Agent 未运行时直接按 original.json 恢复
            var original = AtomicFileStore.ReadJsonOrDefault<OriginalPowerSettings>(paths.OriginalJsonPath);
            if (original is null)
            {
                Console.Error.WriteLine($"未找到原始快照：{paths.OriginalJsonPath}");
                return 1;
            }

            if (dryRun)
            {
                Console.WriteLine($"[DRY-RUN] 将恢复 original.json: AC={original.Ac.Display}/{original.Ac.Sleep}s, DC={original.Dc.Display}/{original.Dc.Sleep}s");
                return 0;
            }

            var scheme = new PowerSchemeSettings(
                original.Ac.Display, original.Ac.Sleep,
                original.Dc.Display, original.Dc.Sleep,
                original.Ac.Lid, original.Dc.Lid,
                original.Ac.Button, original.Dc.Button);
            PowerSchemeInterop.ApplySettings(scheme);
            Console.WriteLine("Agent 未运行；已直接从 original.json 恢复 Windows 原始电源设置。");
            return 0;
        }
    }

    private static int RunBoost(string[] args, AppPaths paths, bool dryRun)
    {
        if (args.Length < 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetStop))
        {
            Console.Error.WriteLine("用法: pmctl boost <85..100> [--hold-min 60] [--max-hours 4]");
            return 1;
        }

        int holdMin = PowerPolicy.BoostHoldMinutesDefault;
        if (GetOption(args, "--hold-min") is string holdStr &&
            !int.TryParse(holdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out holdMin))
        {
            Console.Error.WriteLine($"无效 --hold-min：{holdStr}");
            return 1;
        }

        double maxHours = PowerPolicy.BoostMaxHoursDefault;
        if (GetOption(args, "--max-hours") is string maxStr &&
            !double.TryParse(maxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out maxHours))
        {
            Console.Error.WriteLine($"无效 --max-hours：{maxStr}");
            return 1;
        }

        var boost = new BoostRequest(targetStop, holdMin, maxHours);
        var req = new EcTaskRequest(EcTaskRequestKind.Boost, $"pmctl boost {targetStop}%", boost, DateTimeOffset.UtcNow);
        if (!req.TryValidate(out string? error))
        {
            Console.Error.WriteLine($"参数校验失败：{error}");
            return 1;
        }

        if (dryRun)
        {
            Console.WriteLine($"[DRY-RUN] 校验通过：target={targetStop - PowerPolicy.BoostHysteresisPercent}%–{targetStop}%，保持 {holdMin} 分钟，最长 {maxHours:0.##} 小时。");
            return 0;
        }

        string file = AtomicFileStore.WriteEcRequestAtomic(paths, req);
        var (started, triggerErr) = ScheduledTaskTrigger.TriggerTask(IpcConstants.BatteryChargeTaskName);
        if (!started)
        {
            Console.Error.WriteLine($"已写入请求 {Path.GetFileName(file)}，但触发计划任务失败：{triggerErr}");
            return 1;
        }

        Console.WriteLine($"已发起临时冲高请求（目标 {targetStop - PowerPolicy.BoostHysteresisPercent}%–{targetStop}%，保持 {holdMin} 分钟，最长 {maxHours:0.##} 小时）。");
        return 0;
    }

    private static int PrintPaths(AppPaths paths)
    {
        Console.WriteLine($"DataDir:       {paths.DataDir}");
        Console.WriteLine($"OriginalJson:  {paths.OriginalJsonPath}");
        Console.WriteLine($"ModeJson:      {paths.ModeJsonPath}");
        Console.WriteLine($"AgentLog:      {paths.AgentLogPath}");
        Console.WriteLine($"StateJson:     {paths.StateJsonPath}");
        Console.WriteLine($"HistoryLog:    {paths.HistoryLogPath}");
        Console.WriteLine($"EcLog:         {paths.EcLogPath}");
        Console.WriteLine($"RequestsDir:   {paths.RequestsDir}");
        Console.WriteLine($"PrefsJson:     {paths.PrefsJsonPath}");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知子命令：{command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("PowerManager 控制台工具 (pmctl)");
        Console.WriteLine("用法:");
        Console.WriteLine("  pmctl status [--data-dir <path>] [--pipe-name <name>]");
        Console.WriteLine("  pmctl mode <normal|keep-running> [--hours 2|4|8|12|24] [--pipe-name <name>]");
        Console.WriteLine("  pmctl boost <85..100> [--hold-min 60] [--max-hours 4] [--data-dir <path>] [--dry-run]");
        Console.WriteLine("  pmctl restore-original [--data-dir <path>] [--pipe-name <name>] [--dry-run]");
        Console.WriteLine("  pmctl paths [--data-dir <path>]");
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
