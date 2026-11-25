using System.Collections.Concurrent;
using PowerManager.Core.Contracts;
using PowerManager.Core.Logic;
using PowerManager.Core.Models;
using PowerManager.Core.Policy;
using PowerManager.Platform.Ipc;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.Agent;

/// <summary>
/// 纯事件驱动常驻调度核心（单一 Waitable Timer + Win32 消息循环 + Named Pipe 控制）。
/// 所有状态转移均在主消息循环单线程执行，避免并发状态竞争。
/// </summary>
public sealed class AgentController : IDisposable
{
    private sealed record PendingIpcItem(AgentIpcRequest Request, TaskCompletionSource<AgentIpcResponse> Completion);

    private readonly AppPaths _paths;
    private readonly string _pipeName;
    private readonly bool _dryRun;
    private readonly RotatingUtcLogger _log;
    private readonly RotatingUtcLogger _errorLog;
    private readonly ConcurrentQueue<PendingIpcItem> _ipcQueue = new();
    private readonly AutoResetEvent _ipcSignal = new(false);
    private readonly CancellationTokenSource _cts = new();

    private readonly WaitableTimerHandle _timer = new();
    private readonly AgentMessageWindow _window = new();
    private readonly AgentNamedPipeServer _pipeServer;
    private readonly AgentSystemActions _actions;
    private readonly AgentAlarmScheduler _scheduler;
    private readonly WakeSummaryCoordinator _wakeSummary;

    private ModeState _modeState;
    private bool _acOnline;
    private int _batteryPercent;
    private bool _lidClosed;
    private bool _displayOn = true;

    private ForcedSleepState? _forcedSleep;
    private DateTimeOffset? _normalLidSleepDueUtc;
    private DateTimeOffset? _commuteTempCheckDueUtc;
    private DateTimeOffset? _commuteTempReadDueUtc;

    public AgentController(AppPaths paths, string pipeName, bool dryRun)
    {
        _paths = paths;
        _pipeName = pipeName;
        _dryRun = dryRun;
        _log = new RotatingUtcLogger(paths.AgentLogPath);
        _errorLog = new RotatingUtcLogger(paths.AgentErrorLogPath);

        _paths.EnsureDirectoriesExist();
        _modeState = AtomicFileStore.ReadJsonOrDefault<ModeState>(_paths.ModeJsonPath)
            ?? ModeState.CreateNormal(DateTimeOffset.UtcNow);

        var (ac, pct) = PowerSchemeInterop.GetOsPowerStatus();
        _acOnline = ac ?? true;
        _batteryPercent = pct ?? 100;

        _actions = new AgentSystemActions(_paths, _dryRun, _log, _errorLog);
        _scheduler = new AgentAlarmScheduler(_paths, _timer);
        _wakeSummary = new WakeSummaryCoordinator(_log, _errorLog, _actions.SendToastSafe, _cts.Token);
        _pipeServer = new AgentNamedPipeServer(EnqueueIpcAsync, _pipeName);
        _window.SystemEventReceived += OnSystemEvent;
    }

    public void Run()
    {
        ToastNotifierHelper.SetProcessAppUserModelId();
        _log.Info($"Agent starting (dryRun={_dryRun}, dataDir=\"{_paths.DataDir}\", pipe=\"{_pipeName}\")");

        _actions.EnsureOriginalSnapshot();
        EvaluateModeExpiryIfNeeded(DateTimeOffset.UtcNow);
        _actions.ApplyPowerScheme(_modeState.ParsedMode, _forcedSleep is not null, "startup");
        _actions.ApplyWindowsPowerMode(_acOnline, _batteryPercent, "startup");
        ScheduleCommuteCheckIfNeeded(DateTimeOffset.UtcNow);
        RecomputeAndArmTimer(DateTimeOffset.UtcNow);

        _pipeServer.Start();
        _window.RunEventLoop(
            _timer,
            _ipcSignal.SafeWaitHandle.DangerousGetHandle(),
            OnTimerFired,
            DrainIpcQueue,
            _cts.Token);

        _log.Info("Agent stopped cleanly.");
    }

    private Task<AgentIpcResponse> EnqueueIpcAsync(AgentIpcRequest request)
    {
        var tcs = new TaskCompletionSource<AgentIpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ipcQueue.Enqueue(new PendingIpcItem(request, tcs));
        _ipcSignal.Set();
        return tcs.Task;
    }

    private void DrainIpcQueue()
    {
        while (_ipcQueue.TryDequeue(out var item))
        {
            try
            {
                var response = HandleIpcOnMainThread(item.Request);
                item.Completion.TrySetResult(response);
            }
            catch (Exception ex)
            {
                _errorLog.Error(ex.ToString());
                item.Completion.TrySetResult(new AgentIpcResponse(false, ex.Message));
            }
        }
    }

    private AgentIpcResponse HandleIpcOnMainThread(AgentIpcRequest req)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        switch (req.Command)
        {
            case AgentIpcCommand.Ping:
                return new AgentIpcResponse(true, "pong", _modeState, _forcedSleep is not null, _paths.DataDir);

            case AgentIpcCommand.GetStatus:
                EvaluateModeExpiryIfNeeded(nowUtc);
                return new AgentIpcResponse(true, "ok", _modeState, _forcedSleep is not null, _paths.DataDir);

            case AgentIpcCommand.SetMode:
                return HandleSetModeCommand(req, nowUtc);

            case AgentIpcCommand.RestoreOriginal:
                _actions.RestoreOriginalSettings();
                _cts.Cancel();
                return new AgentIpcResponse(true, "已恢复 Windows 原始电源设置并准备退出常驻。", _modeState, false, _paths.DataDir);

            default:
                return new AgentIpcResponse(false, $"未知命令：{req.Command}");
        }
    }

    private AgentIpcResponse HandleSetModeCommand(AgentIpcRequest req, DateTimeOffset nowUtc)
    {
        if (req.Mode == AgentMode.KeepRunning)
        {
            int hours = req.DurationHours ?? PowerPolicy.KeepRunningDefaultHours;
            if (!PowerModeCalculator.IsValidKeepRunningDuration(hours))
            {
                return new AgentIpcResponse(false, $"不支持的保持运行时长：{hours} 小时（范围 {PowerPolicy.KeepRunningMinHours}–{PowerPolicy.KeepRunningMaxHours} 小时，或 0 表示直到手动改回）。");
            }

            _modeState = ModeState.CreateKeepRunning(nowUtc, hours);
            _normalLidSleepDueUtc = null;
            ClearForcedSleep("switched to keep_running");
            _actions.PersistModeState(_modeState);
            _actions.ApplyPowerScheme(_modeState.ParsedMode, _forcedSleep is not null, hours == 0 ? "set keep_running indefinite" : $"set keep_running {hours}h");
            ScheduleCommuteCheckIfNeeded(nowUtc);
            RecomputeAndArmTimer(nowUtc);
            if (hours == 0)
            {
                _log.Info("Mode switched to keep_running (until manually reverted)");
                return new AgentIpcResponse(true, "已切换至保持运行模式（直到手动改回）。", _modeState, false, _paths.DataDir);
            }

            _log.Info($"Mode switched to keep_running for {hours}h (until {_modeState.RunUntilUtc:yyyy-MM-ddTHH:mm:ssZ})");
            return new AgentIpcResponse(true, $"已切换至保持运行模式（{hours} 小时）。", _modeState, false, _paths.DataDir);
        }

        _modeState = ModeState.CreateNormal(nowUtc);
        _commuteTempCheckDueUtc = null;
        _commuteTempReadDueUtc = null;
        _actions.PersistModeState(_modeState);
        _actions.ApplyPowerScheme(_modeState.ParsedMode, _forcedSleep is not null, "set normal");
        if (SleepGuardEngine.ShouldTriggerNormalLidCloseSleep(AgentMode.Normal, _lidClosed, DisplayAndInputInterop.HasExternalActiveDisplay()))
        {
            _normalLidSleepDueUtc = nowUtc + PowerPolicy.NormalLidCloseSleepDelay;
        }
        RecomputeAndArmTimer(nowUtc);
        _log.Info("Mode switched to normal.");
        return new AgentIpcResponse(true, "已切换至普通模式。", _modeState, _forcedSleep is not null, _paths.DataDir);
    }

    private void OnSystemEvent(PowerSystemEvent ev)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        try
        {
            switch (ev.Kind)
            {
                case PowerSystemEventKind.LidChanged:
                    HandleLidChanged(ev.Value == 0, nowUtc);
                    break;
                case PowerSystemEventKind.DisplayChanged:
                    HandleDisplayChanged(ev.Value != 0, nowUtc);
                    break;
                case PowerSystemEventKind.AcDcChanged:
                    HandleAcDcChanged(ev.Value == 0, nowUtc);
                    break;
                case PowerSystemEventKind.BatteryPercent:
                    HandleBatteryPercentChanged(ev.Value, nowUtc);
                    break;
                case PowerSystemEventKind.SessionLocked:
                    _wakeSummary.OnSessionLocked();
                    break;
                case PowerSystemEventKind.SessionUnlocked:
                    _wakeSummary.OnSessionUnlocked();
                    break;
            }

            RecomputeAndArmTimer(nowUtc);
        }
        catch (Exception ex)
        {
            _errorLog.Error(ex.ToString());
        }
    }

    private void HandleLidChanged(bool closed, DateTimeOffset nowUtc)
    {
        if (_lidClosed == closed)
        {
            return;
        }

        _lidClosed = closed;
        _log.Info($"Lid {(closed ? "closed" : "opened")}");

        if (closed)
        {
            _wakeSummary.MarkAwayStartIfNeeded(nowUtc, _batteryPercent);
            bool hasExt = DisplayAndInputInterop.HasExternalActiveDisplay();
            if (SleepGuardEngine.ShouldTriggerNormalLidCloseSleep(_modeState.ParsedMode, true, hasExt))
            {
                _normalLidSleepDueUtc = nowUtc + PowerPolicy.NormalLidCloseSleepDelay;
            }
        }
        else
        {
            _normalLidSleepDueUtc = null;
            ClearForcedSleep("lid opened");
            if (_displayOn)
            {
                _wakeSummary.TriggerWakeSummaryIfAway(nowUtc, _batteryPercent);
            }
        }
    }

    private void HandleDisplayChanged(bool on, DateTimeOffset nowUtc)
    {
        if (_displayOn == on)
        {
            return;
        }

        _displayOn = on;
        _log.Info($"Display {(on ? "on" : "off")}");

        if (!on)
        {
            _wakeSummary.MarkAwayStartIfNeeded(nowUtc, _batteryPercent);
        }
        else if (!_lidClosed)
        {
            _wakeSummary.TriggerWakeSummaryIfAway(nowUtc, _batteryPercent);
        }
    }

    private void HandleAcDcChanged(bool acOnline, DateTimeOffset nowUtc)
    {
        if (_acOnline == acOnline)
        {
            return;
        }

        _acOnline = acOnline;
        _log.Info($"Power source changed: {(acOnline ? "AC" : "DC")}");
        _actions.ApplyWindowsPowerMode(_acOnline, _batteryPercent, "ac/dc changed");
        ScheduleCommuteCheckIfNeeded(nowUtc);
    }

    private void HandleBatteryPercentChanged(int percent, DateTimeOffset nowUtc)
    {
        int prev = _batteryPercent;
        _batteryPercent = percent;

        if (!_acOnline)
        {
            _actions.ApplyWindowsPowerMode(_acOnline, _batteryPercent, $"dc battery {percent}%");
        }

        if (PowerModeCalculator.ShouldTriggerBatteryHistory(prev, percent))
        {
            _actions.TriggerEcTask($"电量 {percent}%", nowUtc);
        }
    }

    private void OnTimerFired()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        try
        {
            if (_scheduler.IsTimerFrozen(nowUtc, out double driftSec))
            {
                _log.Info($"Timer frozen wake detected (drift={driftSec:F1}s, lidClosed={_lidClosed}, mode={_modeState.ParsedMode}, ac={_acOnline})");

                if (_forcedSleep is not null)
                {
                    ClearForcedSleep("woke from frozen sleep");
                }

                if (SleepGuardEngine.ShouldTriggerUnexpectedWakeFallback(true, _lidClosed, _modeState.ParsedMode, _acOnline))
                {
                    EnterForcedSleep("合盖意外唤醒兜底", nowUtc);
                }
            }

            // 1. 保持运行到期检查（SLP-02）
            if (EvaluateModeExpiryIfNeeded(nowUtc))
            {
                _actions.ApplyPowerScheme(_modeState.ParsedMode, _forcedSleep is not null, "keep_running expired");
                _actions.SendToastSafe("保持运行到期", "已回到普通模式：插电闲置 1 小时睡，用电池 15 分钟睡，合盖 1 分钟睡", tag: "mode");
                if (SleepGuardEngine.ShouldTriggerNormalLidCloseSleep(AgentMode.Normal, _lidClosed, DisplayAndInputInterop.HasExternalActiveDisplay()))
                {
                    EnterForcedSleep("保持运行到期且合盖", nowUtc);
                }
            }

            // 2. 普通模式合盖 1 分钟到期（SLP-06）
            if (_normalLidSleepDueUtc.HasValue && nowUtc >= _normalLidSleepDueUtc.Value)
            {
                _normalLidSleepDueUtc = null;
                if (SleepGuardEngine.ShouldTriggerNormalLidCloseSleep(_modeState.ParsedMode, _lidClosed, DisplayAndInputInterop.HasExternalActiveDisplay()))
                {
                    EnterForcedSleep("合盖 1 分钟", nowUtc);
                }
            }

            // 3. 通勤电池温度检查与 32 分钟闲置接管（SLP-05）
            ProcessCommuteChecks(nowUtc);

            // 4. 主动睡眠每 1 分钟检查与 2 分钟休眠兜底（SLP-10）
            ProcessForcedSleepTick(nowUtc);

            // 5. 检查冲高回落到期时间点，主动唤醒 EC 任务执行回落（CHG-02）
            if (_scheduler.IsBoostRevertDue(_acOnline, nowUtc))
            {
                _actions.TriggerEcTask("冲高到期回落检查", nowUtc);
            }

            RecomputeAndArmTimer(nowUtc);
        }
        catch (Exception ex)
        {
            _errorLog.Error(ex.ToString());
        }
    }

    private void ProcessCommuteChecks(DateTimeOffset nowUtc)
    {
        if (_commuteTempCheckDueUtc.HasValue && nowUtc >= _commuteTempCheckDueUtc.Value)
        {
            _commuteTempCheckDueUtc = null;
            if (_modeState.ParsedMode == AgentMode.KeepRunning && !_acOnline)
            {
                TimeSpan idle = DisplayAndInputInterop.GetUserIdleDuration();
                if (SleepGuardEngine.ShouldTakeoverCommuteIdle(_modeState.ParsedMode, _acOnline, idle, _forcedSleep is not null))
                {
                    EnterForcedSleep("通勤电池闲置 32 分钟接管", nowUtc);
                }
                else
                {
                    _actions.TriggerEcTask("通勤查温", nowUtc);
                    _commuteTempReadDueUtc = nowUtc + PowerPolicy.CommuteTempReadDelayAfterTrigger;
                }

                _commuteTempCheckDueUtc = nowUtc + PowerPolicy.CommuteTempCheckInterval;
            }
        }

        if (_commuteTempReadDueUtc.HasValue && nowUtc >= _commuteTempReadDueUtc.Value)
        {
            _commuteTempReadDueUtc = null;
            if (_modeState.ParsedMode == AgentMode.KeepRunning && !_acOnline && _forcedSleep is null)
            {
                var state = AtomicFileStore.ReadJsonOrDefault<ChargeStateSnapshot>(_paths.StateJsonPath);
                TimeSpan idle = DisplayAndInputInterop.GetUserIdleDuration();
                var decision = SleepGuardEngine.EvaluateCommuteTemperature(
                    state?.Ec.BatteryC,
                    state?.UpdatedAtUtc,
                    _lidClosed,
                    idle,
                    nowUtc);

                if (decision == CommuteTempDecision.TriggerOverheatSleep)
                {
                    EnterForcedSleep($"电池过热 {state!.Ec.BatteryC:F1} °C", nowUtc);
                }
                else if (decision == CommuteTempDecision.SkipDueToActiveUser)
                {
                    _log.Info($"Commute overheat ({state?.Ec.BatteryC:F1} C) skipped: lid open & active user (idle={idle.TotalSeconds:0}s)");
                }
            }
        }
    }

    private void ProcessForcedSleepTick(DateTimeOffset nowUtc)
    {
        if (_forcedSleep is null)
        {
            return;
        }

        TimeSpan idle = DisplayAndInputInterop.GetUserIdleDuration();
        var tickAction = SleepGuardEngine.EvaluateForcedSleepTick(_forcedSleep, _lidClosed, idle, nowUtc);
        if (tickAction == ForcedSleepTickAction.AbortDueToUserActivity)
        {
            _log.Info($"Forced sleep aborted: lid open & user active (idle={idle.TotalSeconds:0}s)");
            ClearForcedSleep("user active abort");
        }
        else if (tickAction == ForcedSleepTickAction.TriggerHibernate)
        {
            _log.Warn($"Forced sleep timed out after 2 min (note=\"{_forcedSleep.TriggerNote}\"), invoking Hibernate fallback.");
            _wakeSummary.AddNote("2 分钟未入睡转休眠");
            if (!_actions.TryHibernate())
            {
                _forcedSleep = SleepGuardEngine.ResetAfterHibernateRejected(_forcedSleep, nowUtc);
            }
        }
    }

    private void RecomputeAndArmTimer(DateTimeOffset nowUtc) =>
        _scheduler.RecomputeAndArm(
            _modeState,
            _acOnline,
            _normalLidSleepDueUtc,
            _commuteTempCheckDueUtc,
            _commuteTempReadDueUtc,
            _forcedSleep is not null,
            nowUtc);

    private void EnterForcedSleep(string note, DateTimeOffset nowUtc)
    {
        if (_forcedSleep is not null)
        {
            return;
        }

        _forcedSleep = new ForcedSleepState(nowUtc, note);
        _wakeSummary.AddNote(note);
        _log.Info($"Entering forced sleep (10s display / 15s sleep): {note}");
        _actions.ApplyPowerScheme(_modeState.ParsedMode, forcedSleepActive: true, $"forced sleep: {note}");
    }

    private void ClearForcedSleep(string reason)
    {
        if (_forcedSleep is null)
        {
            return;
        }

        _forcedSleep = null;
        _log.Info($"Cleared forced sleep ({reason})");
        _actions.ApplyPowerScheme(_modeState.ParsedMode, forcedSleepActive: false, $"clear forced sleep: {reason}");
    }

    private void ScheduleCommuteCheckIfNeeded(DateTimeOffset nowUtc)
    {
        if (_modeState.ParsedMode == AgentMode.KeepRunning && !_acOnline)
        {
            _commuteTempCheckDueUtc ??= nowUtc + PowerPolicy.CommuteTempCheckInterval;
        }
        else
        {
            _commuteTempCheckDueUtc = null;
            _commuteTempReadDueUtc = null;
        }
    }

    private bool EvaluateModeExpiryIfNeeded(DateTimeOffset nowUtc)
    {
        var (next, expired) = PowerModeCalculator.EvaluateModeExpiry(_modeState, nowUtc);
        if (expired)
        {
            _modeState = next;
            _commuteTempCheckDueUtc = null;
            _commuteTempReadDueUtc = null;
            _actions.PersistModeState(_modeState);
            _log.Info("KeepRunning expired; reverted to normal mode.");
        }

        return expired;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipeServer.Dispose();
        _window.Dispose();
        _timer.Dispose();
        _ipcSignal.Dispose();
        _cts.Dispose();
    }
}
