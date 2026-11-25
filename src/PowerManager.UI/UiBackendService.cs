using System.Diagnostics;
using PowerManager.Core.Contracts;
using PowerManager.Core.Models;
using PowerManager.Core.Policy;
using PowerManager.Platform.Ipc;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.UI;

public sealed class UiBackendService
{
    private readonly AppPaths _paths;
    private readonly string _pipeName;
    private readonly bool _dryRun;

    public UiBackendService(AppPaths paths, string pipeName, bool dryRun)
    {
        _paths = paths;
        _pipeName = pipeName;
        _dryRun = dryRun;
    }

    public static string TranslateRevertReason(string? text) => UiStateFormatter.TranslateRevertReason(text);

    public bool GetRulesExpandedPref() => LoadPrefs().RulesExpanded;

    public void SaveRulesExpandedPref(bool expanded)
    {
        var prefs = LoadPrefs();
        if (prefs.RulesExpanded != expanded)
        {
            SavePrefs(prefs with { RulesExpanded = expanded });
        }
    }

    public async Task<UiStateView> GetStateAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var prefs = LoadPrefs();

        bool agentOnline = false;
        bool forcedSleep = false;
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
                forcedSleep = resp.ForcedSleepActive;
                modeState = resp.ModeState;
            }
        }
        catch
        {
            agentOnline = false;
        }

        modeState ??= ReadJsonSafe<ModeState>(_paths.ModeJsonPath) ?? ModeState.CreateNormal(nowUtc);

        var (osAc, osPct) = PowerSchemeInterop.GetOsPowerStatus();
        var state = ReadJsonSafe<ChargeStateSnapshot>(_paths.StateJsonPath);
        bool acOnline = osAc ?? state?.OsPower.AcOnline ?? false;
        int? pct = osPct ?? state?.OsPower.Percent;

        var powerModeView = new UiPowerModeView(
            Ac: UiStateFormatter.FormatPowerModeZh(PowerSchemeInterop.GetUserPowerMode(ac: true)),
            Dc: UiStateFormatter.FormatPowerModeZh(PowerSchemeInterop.GetUserPowerMode(ac: false)));

        return new UiStateView(
            Agent: agentOnline,
            DryRun: _dryRun,
            ForcedSleep: forcedSleep,
            RulesExpanded: prefs.RulesExpanded,
            DataDir: _paths.DataDir,
            Mode: UiStateFormatter.BuildModeView(modeState, prefs.Hours, nowUtc),
            PowerMode: powerModeView,
            Battery: UiStateFormatter.BuildBatteryView(acOnline, pct, state, nowUtc),
            Boost: UiStateFormatter.BuildBoostView(state?.Boost, nowUtc),
            BoostPrefs: UiStateFormatter.BuildBoostPrefsView(prefs));
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
            SavePrefs(LoadPrefs() with { Hours = resolvedHours });
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

    public Task<UiActionResult> RequestBoostAsync(int target, int holdMinutes, double maxHours, CancellationToken ct = default)
    {
        var boostReq = new BoostRequest(target, holdMinutes, maxHours);
        if (!boostReq.TryValidate(out string? err))
        {
            return Task.FromResult(new UiActionResult(false, err ?? "冲高参数超出允许范围。"));
        }

        SavePrefs(LoadPrefs() with
        {
            BoostTarget = target,
            BoostHold = holdMinutes,
            BoostMaxHours = maxHours
        });

        var ecReq = new EcTaskRequest(
            Kind: EcTaskRequestKind.Boost,
            Reason: "ui-boost",
            Boost: boostReq,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        return SubmitEcTaskRequestAsync(
            ecReq,
            writeErrPrefix: "写入冲高请求失败",
            dryRunPrefix: "[dry-run] 已写入冲高请求文件",
            taskErrSuffix: "：请以管理员身份运行一次 install.ps1，或查看 battery-charge\\error.log。",
            successMsg: null,
            ct);
    }

    /// <summary>
    /// 按需触发一次 Battery-ChargeLimit 刷新 EC 温度/功耗/阈值读数（零轮询，无 UAC）。
    /// </summary>
    public Task<UiActionResult> RefreshEcAsync(CancellationToken ct = default)
    {
        var ecReq = new EcTaskRequest(
            Kind: EcTaskRequestKind.Trigger,
            Reason: "ui-refresh",
            Boost: null,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        return SubmitEcTaskRequestAsync(
            ecReq,
            writeErrPrefix: "写入刷新请求失败",
            dryRunPrefix: "[dry-run] 已写入刷新请求",
            taskErrSuffix: "。",
            successMsg: "已刷新 EC 读数",
            ct);
    }

    public UiActionResult OpenDataDir()
    {
        try
        {
            _paths.EnsureDirectoriesExist();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.DataDir}\"",
                UseShellExecute = true
            });
            return new UiActionResult(true);
        }
        catch (Exception ex)
        {
            return new UiActionResult(false, $"无法打开数据目录：{ex.Message}");
        }
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

    private async Task<UiActionResult> SubmitEcTaskRequestAsync(
        EcTaskRequest ecReq,
        string writeErrPrefix,
        string dryRunPrefix,
        string taskErrSuffix,
        string? successMsg,
        CancellationToken ct)
    {
        string reqPath;
        try
        {
            _paths.EnsureDirectoriesExist();
            reqPath = AtomicFileStore.WriteEcRequestAtomic(_paths, ecReq);
        }
        catch (Exception ex)
        {
            return new UiActionResult(false, $"{writeErrPrefix}：{ex.Message}");
        }

        if (_dryRun)
        {
            return new UiActionResult(true, $"{dryRunPrefix}：{Path.GetFileName(reqPath)}");
        }

        var (started, triggerErr) = ScheduledTaskTrigger.TriggerTask(IpcConstants.BatteryChargeTaskName);
        if (!started)
        {
            return new UiActionResult(
                false,
                $"计划任务 {IpcConstants.BatteryChargeTaskName} 没有响应（{triggerErr ?? "未知错误"}）{taskErrSuffix}");
        }

        await WaitForRequestConsumedAsync(reqPath, ct).ConfigureAwait(false);
        return new UiActionResult(true, successMsg);
    }

    private static async Task WaitForRequestConsumedAsync(string reqPath, CancellationToken ct)
    {
        for (int i = 0; i < 25; i++)
        {
            if (!File.Exists(reqPath))
            {
                // 给 state.json 原子落盘留极短缓冲
                await Task.Delay(120, ct).ConfigureAwait(false);
                break;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

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
}
