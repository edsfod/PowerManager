using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PowerManager.Core.Contracts;
using PowerManager.Core.Models;
using PowerManager.Core.Policy;
using PowerManager.Platform.Ipc;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.UI;

public sealed record UiPrefs(
    [property: JsonPropertyName("hours")] int Hours = PowerPolicy.KeepRunningDefaultHours,
    [property: JsonPropertyName("boost_target")] int BoostTarget = 95,
    [property: JsonPropertyName("boost_hold")] int BoostHold = PowerPolicy.BoostHoldMinutesDefault,
    [property: JsonPropertyName("boost_maxh")] double BoostMaxHours = PowerPolicy.BoostMaxHoursDefault);

public sealed record UiActionResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("msg")] string? Msg = null);

public sealed record UiModeView(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("hours")] int Hours,
    [property: JsonPropertyName("until")] string? Until,
    [property: JsonPropertyName("left")] string? Left);

public sealed record UiPowerModeView(
    [property: JsonPropertyName("ac")] string Ac,
    [property: JsonPropertyName("dc")] string Dc);

public sealed record UiBatteryView(
    [property: JsonPropertyName("pct")] int? Pct,
    [property: JsonPropertyName("ac")] bool? Ac,
    [property: JsonPropertyName("flow")] string Flow,
    [property: JsonPropertyName("band")] int[] Band,
    [property: JsonPropertyName("hw")] int[]? Hw,
    [property: JsonPropertyName("temp")] double? Temp,
    [property: JsonPropertyName("system_w")] int? SystemW,
    [property: JsonPropertyName("charge_ma")] int? ChargeMa,
    [property: JsonPropertyName("hottest")] double? Hottest,
    [property: JsonPropertyName("read_at")] string? ReadAt,
    [property: JsonPropertyName("slow")] bool Slow,
    [property: JsonPropertyName("last")] string Last);

public sealed record UiBoostView(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("target")] int? Target = null,
    [property: JsonPropertyName("hold")] int? Hold = null,
    [property: JsonPropertyName("held")] int? Held = null,
    [property: JsonPropertyName("deadline")] string? Deadline = null);

public sealed record UiBoostPrefsView(
    [property: JsonPropertyName("target")] int Target,
    [property: JsonPropertyName("hold")] int Hold,
    [property: JsonPropertyName("maxh")] double MaxHours);

public sealed record UiStateView(
    [property: JsonPropertyName("agent")] bool Agent,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("mode")] UiModeView Mode,
    [property: JsonPropertyName("power_mode")] UiPowerModeView PowerMode,
    [property: JsonPropertyName("battery")] UiBatteryView Battery,
    [property: JsonPropertyName("boost")] UiBoostView Boost,
    [property: JsonPropertyName("boost_prefs")] UiBoostPrefsView BoostPrefs);

public sealed partial class UiBackendService
{
    private const int LowChargeMaThreshold = 1000;

    private readonly AppPaths _paths;
    private readonly string _pipeName;
    private readonly bool _dryRun;

    public UiBackendService(AppPaths paths, string pipeName, bool dryRun)
    {
        _paths = paths;
        _pipeName = pipeName;
        _dryRun = dryRun;
    }

    public async Task<UiStateView> GetStateAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var prefs = LoadPrefs();

        bool agentOnline = false;
        ModeState? modeState = null;
        try
        {
            var resp = await AgentNamedPipeClient.SendAsync(
                new AgentIpcRequest(AgentIpcCommand.GetStatus),
                _pipeName,
                timeoutMs: 800,
                ct: ct).ConfigureAwait(false);
            if (resp.Ok)
            {
                agentOnline = true;
                modeState = resp.ModeState;
            }
        }
        catch
        {
            agentOnline = false;
        }

        modeState ??= ReadJsonSafe<ModeState>(_paths.ModeJsonPath) ?? ModeState.CreateNormal(nowUtc);

        UiModeView modeView = BuildModeView(modeState, prefs.Hours, nowUtc);

        var (osAc, osPct) = PowerSchemeInterop.GetOsPowerStatus();
        var state = ReadJsonSafe<ChargeStateSnapshot>(_paths.StateJsonPath);
        var ec = state?.Ec;
        var win = state?.Win;
        var boost = state?.Boost;

        bool acOnline = osAc ?? state?.OsPower.AcOnline ?? false;
        int? pct = osPct ?? state?.OsPower.Percent;
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

        string lastReason = boost is { Active: true }
            ? string.Empty
            : TranslateRevertReason(boost?.LastReason);

        var batteryView = new UiBatteryView(
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
            Slow: slow,
            Last: lastReason);

        UiBoostView boostView = BuildBoostView(boost, nowUtc);

        var powerModeView = new UiPowerModeView(
            Ac: FormatPowerModeZh(PowerSchemeInterop.GetUserPowerMode(ac: true)),
            Dc: FormatPowerModeZh(PowerSchemeInterop.GetUserPowerMode(ac: false)));

        var boostPrefsView = new UiBoostPrefsView(
            Target: Math.Clamp(prefs.BoostTarget, PowerPolicy.BoostTargetMinPercent, PowerPolicy.BoostTargetMaxPercent),
            Hold: Math.Clamp(prefs.BoostHold, PowerPolicy.BoostHoldMinutesMin, PowerPolicy.BoostHoldMinutesMax),
            MaxHours: Math.Clamp(prefs.BoostMaxHours, PowerPolicy.BoostMaxHoursMin, PowerPolicy.BoostMaxHoursMax));

        return new UiStateView(
            Agent: agentOnline,
            DryRun: _dryRun,
            Mode: modeView,
            PowerMode: powerModeView,
            Battery: batteryView,
            Boost: boostView,
            BoostPrefs: boostPrefsView);
    }

    public async Task<UiActionResult> SetModeAsync(string mode, int? hours, CancellationToken ct = default)
    {
        bool isRun = string.Equals(mode, "run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, ModeState.ModeKeepRunningValue, StringComparison.OrdinalIgnoreCase);

        int resolvedHours = hours.HasValue && PowerPolicy.KeepRunningDurationHoursOptions.Contains(hours.Value)
            ? hours.Value
            : PowerPolicy.KeepRunningDefaultHours;

        if (isRun)
        {
            var prefs = LoadPrefs() with { Hours = resolvedHours };
            SavePrefs(prefs);
        }

        try
        {
            var req = isRun
                ? new AgentIpcRequest(AgentIpcCommand.SetMode, AgentMode.KeepRunning, resolvedHours)
                : new AgentIpcRequest(AgentIpcCommand.SetMode, AgentMode.Normal);

            var resp = await AgentNamedPipeClient.SendAsync(req, _pipeName, timeoutMs: 3000, ct: ct)
                .ConfigureAwait(false);
            return resp.Ok
                ? new UiActionResult(true)
                : new UiActionResult(false, resp.Message ?? "切换模式失败。");
        }
        catch
        {
            return new UiActionResult(false, "常驻程序没在运行：先点右上角“启动”。");
        }
    }

    public async Task<UiActionResult> RequestBoostAsync(int target, int holdMinutes, double maxHours, CancellationToken ct = default)
    {
        var boostReq = new BoostRequest(target, holdMinutes, maxHours);
        if (!boostReq.TryValidate(out string? err))
        {
            return new UiActionResult(false, err ?? "冲高参数超出允许范围。");
        }

        var prefs = LoadPrefs() with
        {
            BoostTarget = target,
            BoostHold = holdMinutes,
            BoostMaxHours = maxHours
        };
        SavePrefs(prefs);

        var ecReq = new EcTaskRequest(
            Kind: EcTaskRequestKind.Boost,
            Reason: "ui-boost",
            Boost: boostReq,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        string reqPath;
        try
        {
            _paths.EnsureDirectoriesExist();
            reqPath = AtomicFileStore.WriteEcRequestAtomic(_paths, ecReq);
        }
        catch (Exception ex)
        {
            return new UiActionResult(false, $"写入冲高请求失败：{ex.Message}");
        }

        if (_dryRun)
        {
            return new UiActionResult(true, $"[dry-run] 已写入冲高请求文件：{Path.GetFileName(reqPath)}");
        }

        var (started, triggerErr) = ScheduledTaskTrigger.TriggerTask(IpcConstants.BatteryChargeTaskName);
        if (!started)
        {
            return new UiActionResult(
                false,
                $"计划任务 {IpcConstants.BatteryChargeTaskName} 没有响应（{triggerErr ?? "未知错误"}）：请以管理员身份运行一次 install.ps1，或查看 battery-charge\\error.log。");
        }

        // 等待提权任务消费请求文件（最多 2.5 秒），确保随后刷新的状态已更新
        for (int i = 0; i < 25; i++)
        {
            if (!File.Exists(reqPath))
            {
                break;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return new UiActionResult(true);
    }

    public async Task<UiActionResult> StartAgentAsync(CancellationToken ct = default)
    {
        if (_dryRun)
        {
            return new UiActionResult(false, "[dry-run] 已跳过触发生产计划任务 PowerManager-Agent。");
        }

        var (started, err) = ScheduledTaskTrigger.TriggerTask(IpcConstants.AgentTaskName);
        if (!started)
        {
            return new UiActionResult(false, $"没能启动常驻程序（{err ?? "未知错误"}）：请以管理员身份运行一次 install.ps1。");
        }

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            try
            {
                var ping = await AgentNamedPipeClient.SendAsync(
                    new AgentIpcRequest(AgentIpcCommand.Ping),
                    _pipeName,
                    timeoutMs: 400,
                    ct: ct).ConfigureAwait(false);
                if (ping.Ok)
                {
                    return new UiActionResult(true);
                }
            }
            catch
            {
                // 等待常驻启动完成
            }
        }

        return new UiActionResult(false, "没能启动常驻程序：请以管理员身份运行一次 install.ps1。");
    }

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

    private static UiModeView BuildModeView(ModeState modeState, int prefHours, DateTimeOffset nowUtc)
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
            return new UiModeView("run", resolvedHours, untilLocal.ToString("HH:mm"), left);
        }

        return new UiModeView("normal", resolvedHours, null, null);
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

    private static UiBoostView BuildBoostView(BoostState? boost, DateTimeOffset nowUtc)
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
            Deadline: deadline);
    }

    private static string FormatPowerModeZh(WindowsPowerMode? mode) => mode switch
    {
        WindowsPowerMode.BestPowerEfficiency => "最佳能效",
        WindowsPowerMode.Balanced => "平衡",
        WindowsPowerMode.BestPerformance => "最佳性能",
        _ => "–"
    };

    private UiPrefs LoadPrefs() => ReadJsonSafe<UiPrefs>(_paths.PrefsJsonPath) ?? new UiPrefs();

    private void SavePrefs(UiPrefs prefs)
    {
        try
        {
            _paths.EnsureDirectoriesExist();
            AtomicFileStore.WriteJsonAtomic(_paths.PrefsJsonPath, prefs);
        }
        catch
        {
            // 偏好写入失败不阻断核心控制流程
        }
    }

    private static T? ReadJsonSafe<T>(string path)
    {
        try
        {
            return AtomicFileStore.ReadJsonOrDefault<T>(path);
        }
        catch
        {
            return default;
        }
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
