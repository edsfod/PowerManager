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

    private ModeState _modeState;
    private bool _acOnline;
    private int _batteryPercent;
    private bool _lidClosed;
    private bool _displayOn = true;
    private bool _sessionLocked;

    private ForcedSleepState? _forcedSleep;
    private DateTimeOffset? _normalLidSleepDueUtc;
    private DateTimeOffset? _commuteTempCheckDueUtc;
    private DateTimeOffset? _commuteTempReadDueUtc;

    private DateTimeOffset? _awayStartUtc;
    private int? _awayStartBattery;
    private readonly List<string> _awayNotes = [];
    private (string Title, string Body)? _pendingUnlockToast;
    private readonly object _toastLock = new();

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

        _pipeServer = new AgentNamedPipeServer(EnqueueIpcAsync, _pipeName);
        _window.SystemEventReceived += OnSystemEvent;
    }

    public void Run()
    {
        ToastNotifierHelper.SetProcessAppUserModelId();
        _log.Info($"Agent starting (dryRun={_dryRun}, dataDir=\"{_paths.DataDir}\", pipe=\"{_pipeName}\")");

        EnsureOriginalSnapshot();
        EvaluateModeExpiryIfNeeded(DateTimeOffset.UtcNow);
        ApplyCurrentPowerScheme("startup");
        ApplyCurrentWindowsPowerMode("startup");
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
                if (req.Mode == AgentMode.KeepRunning)
                {
                    int hours = req.DurationHours ?? PowerPolicy.KeepRunningDefaultHours;
                    if (!PowerModeCalculator.IsValidKeepRunningDuration(hours))
                    {
                        return new AgentIpcResponse(false, $"不支持的保持运行时长：{hours} 小时（可选 2/4/8/12/24）。");
                    }

                    _modeState = ModeState.CreateKeepRunning(nowUtc, hours);
                    _normalLidSleepDueUtc = null;
                    ClearForcedSleep("switched to keep_running");
                    PersistModeState();
                    ApplyCurrentPowerScheme($"set keep_running {hours}h");
                    ScheduleCommuteCheckIfNeeded(nowUtc);
                    RecomputeAndArmTimer(nowUtc);
                    _log.Info($"Mode switched to keep_running for {hours}h (until {_modeState.RunUntilUtc:yyyy-MM-ddTHH:mm:ssZ})");
                    return new AgentIpcResponse(true, $"已切换至保持运行模式（{hours} 小时）。", _modeState, false, _paths.DataDir);
                }
                else
                {
                    _modeState = ModeState.CreateNormal(nowUtc);
                    _commuteTempCheckDueUtc = null;
                    _commuteTempReadDueUtc = null;
                    PersistModeState();
                    ApplyCurrentPowerScheme("set normal");
                    if (SleepGuardEngine.ShouldTriggerNormalLidCloseSleep(AgentMode.Normal, _lidClosed, DisplayAndInputInterop.HasExternalActiveDisplay()))
                    {
                        _normalLidSleepDueUtc = nowUtc + PowerPolicy.NormalLidCloseSleepDelay;
                    }
                    RecomputeAndArmTimer(nowUtc);
                    _log.Info("Mode switched to normal.");
                    return new AgentIpcResponse(true, "已切换至普通模式。", _modeState, _forcedSleep is not null, _paths.DataDir);
                }

            case AgentIpcCommand.RestoreOriginal:
                RestoreOriginalSettingsAndStop();
                return new AgentIpcResponse(true, "已恢复 Windows 原始电源设置并准备退出常驻。", _modeState, false, _paths.DataDir);

            default:
                return new AgentIpcResponse(false, $"未知命令：{req.Command}");
        }
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
                    _sessionLocked = true;
                    break;
                case PowerSystemEventKind.SessionUnlocked:
                    _sessionLocked = false;
                    FlushPendingToastIfUnlocked();
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
            MarkAwayStartIfNeeded(nowUtc);
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
                TriggerWakeSummaryIfAway(nowUtc);
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
            MarkAwayStartIfNeeded(nowUtc);
        }
        else if (!_lidClosed)
        {
            TriggerWakeSummaryIfAway(nowUtc);
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
        ApplyCurrentWindowsPowerMode("ac/dc changed");
        ScheduleCommuteCheckIfNeeded(nowUtc);
    }

    private void HandleBatteryPercentChanged(int percent, DateTimeOffset nowUtc)
    {
        int prev = _batteryPercent;
        _batteryPercent = percent;

        if (!_acOnline)
        {
            ApplyCurrentWindowsPowerMode($"dc battery {percent}%");
        }

        if (PowerModeCalculator.ShouldTriggerBatteryHistory(prev, percent))
        {
            TriggerEcTask($"电量 {percent}%", nowUtc);
        }
    }

    private void OnTimerFired()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        try
        {
            bool frozen = _timer.ScheduledTargetUtc.HasValue &&
                          WakeAlarmPlanner.IsTimerFrozen(_timer.ScheduledTargetUtc.Value, nowUtc);

            if (frozen)
            {
                double driftSec = (nowUtc - _timer.ScheduledTargetUtc!.Value).TotalSeconds;
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
                ApplyCurrentPowerScheme("keep_running expired");
                SendToastSafe("PowerManager", "保持运行已到期，已自动切回普通模式。");
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
                        TriggerEcTask("通勤查温", nowUtc);
                        _commuteTempReadDueUtc = nowUtc + PowerPolicy.CommuteTempReadDelayAfterTrigger;
                    }

                    _commuteTempCheckDueUtc = nowUtc + PowerPolicy.CommuteTempCheckInterval;
                }
            }

            // 4. 10 秒后读取通勤电池温度结果（SLP-05）
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

            // 5. 主动睡眠每 1 分钟检查与 2 分钟休眠兜底（SLP-10）
            if (_forcedSleep is not null)
            {
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
                    _awayNotes.Add("2 分钟未入睡转休眠");
                    if (_dryRun)
                    {
                        _log.Info("[DRY-RUN] Skipped SetSuspendState(Hibernate).");
                        _forcedSleep = SleepGuardEngine.ResetAfterHibernateRejected(_forcedSleep, nowUtc);
                    }
                    else if (!PowerSchemeInterop.TryHibernate())
                    {
                        _log.Warn("SetSuspendState(Hibernate) rejected by Windows; resetting timer for next retry.");
                        _forcedSleep = SleepGuardEngine.ResetAfterHibernateRejected(_forcedSleep, nowUtc);
                    }
                }
            }

            // 6. 检查冲高回落到期时间点，主动唤醒 EC 任务执行回落（CHG-02）
            TriggerBoostCheckIfDue(nowUtc);

            RecomputeAndArmTimer(nowUtc);
        }
        catch (Exception ex)
        {
            _errorLog.Error(ex.ToString());
        }
    }

    private void RecomputeAndArmTimer(DateTimeOffset nowUtc)
    {
        var candidates = new List<AlarmCandidate>();

        if (_modeState.ParsedMode == AgentMode.KeepRunning && _modeState.RunUntilUtc.HasValue)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.KeepRunningExpiry, _modeState.RunUntilUtc.Value));
        }

        if (_normalLidSleepDueUtc.HasValue)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.NormalLidCloseSleep, _normalLidSleepDueUtc.Value));
        }

        if (_commuteTempCheckDueUtc.HasValue)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.CommuteTempCheck, _commuteTempCheckDueUtc.Value));
        }

        if (_commuteTempReadDueUtc.HasValue)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.CommuteTempReadResult, _commuteTempReadDueUtc.Value));
        }

        if (_forcedSleep is not null)
        {
            candidates.Add(new AlarmCandidate(AlarmKind.ForcedSleepCheck, nowUtc + PowerPolicy.ForcedSleepCheckInterval));
        }

        // 读取 state.json 中的冲高定时器点
        var chargeState = AtomicFileStore.ReadJsonOrDefault<ChargeStateSnapshot>(_paths.StateJsonPath);
        if (chargeState?.Boost.Active == true)
        {
            var b = chargeState.Boost;
            if (b.DeadlineUtc.HasValue)
            {
                candidates.Add(new AlarmCandidate(AlarmKind.BoostDeadline, b.DeadlineUtc.Value));
            }
            if (_acOnline && b.FullSinceUtc.HasValue)
            {
                candidates.Add(new AlarmCandidate(AlarmKind.BoostFullHoldComplete, b.FullSinceUtc.Value.AddMinutes(b.FullHoldMinutes)));
            }
            if (!_acOnline && !b.ReachedTarget && b.UnpluggedSinceUtc.HasValue)
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

    private void EnterForcedSleep(string note, DateTimeOffset nowUtc)
    {
        if (_forcedSleep is not null)
        {
            return;
        }

        _forcedSleep = new ForcedSleepState(nowUtc, note);
        _awayNotes.Add(note);
        _log.Info($"Entering forced sleep (10s display / 15s sleep): {note}");
        ApplyCurrentPowerScheme($"forced sleep: {note}");
    }

    private void ClearForcedSleep(string reason)
    {
        if (_forcedSleep is null)
        {
            return;
        }

        _forcedSleep = null;
        _log.Info($"Cleared forced sleep ({reason})");
        ApplyCurrentPowerScheme($"clear forced sleep: {reason}");
    }

    private void MarkAwayStartIfNeeded(DateTimeOffset nowUtc)
    {
        if (_awayStartUtc is not null)
        {
            return;
        }

        _awayStartUtc = nowUtc;
        _awayStartBattery = _batteryPercent;
        _awayNotes.Clear();
    }

    private void TriggerWakeSummaryIfAway(DateTimeOffset nowUtc)
    {
        if (_awayStartUtc is null)
        {
            return;
        }

        DateTimeOffset awayStart = _awayStartUtc.Value;
        int? startBat = _awayStartBattery;
        int endBat = _batteryPercent;
        var notesSnapshot = _awayNotes.ToArray();

        _awayStartUtc = null;
        _awayStartBattery = null;
        _awayNotes.Clear();

        if (!WakeSummaryFormatter.ShouldNotify(awayStart, nowUtc))
        {
            return;
        }

        // 后台线程延迟 15 秒查询 wevtapi（docs/design.md §二.3）
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PowerPolicy.WakeLogQueryDelay, _cts.Token).ConfigureAwait(false);
                var (winStart, winEnd) = WakeSummaryFormatter.GetQueryWindow(awayStart, nowUtc);
                var events = EventLogReaderInterop.QueryPowerEvents(winStart, winEnd);
                var summary = WakeSummaryFormatter.AnalyzeAndFormat(
                    new WakeSummaryInput(awayStart, nowUtc, startBat, endBat, notesSnapshot, events),
                    TimeZoneInfo.Local);

                _log.Info($"Wake summary: {summary.Title} | {summary.Body.Replace(Environment.NewLine, " / ")}");

                if (_sessionLocked)
                {
                    lock (_toastLock)
                    {
                        _pendingUnlockToast = (summary.Title, summary.Body);
                    }
                }
                else
                {
                    SendToastSafe(summary.Title, summary.Body);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                _errorLog.Error(ex.ToString());
            }
        });
    }

    private void FlushPendingToastIfUnlocked()
    {
        (string Title, string Body)? pending;
        lock (_toastLock)
        {
            pending = _pendingUnlockToast;
            _pendingUnlockToast = null;
        }

        if (pending.HasValue)
        {
            SendToastSafe(pending.Value.Title, pending.Value.Body);
        }
    }

    private void TriggerBoostCheckIfDue(DateTimeOffset nowUtc)
    {
        var state = AtomicFileStore.ReadJsonOrDefault<ChargeStateSnapshot>(_paths.StateJsonPath);
        if (state?.Boost.Active != true)
        {
            return;
        }

        var b = state.Boost;
        bool due =
            (b.DeadlineUtc.HasValue && nowUtc >= b.DeadlineUtc.Value) ||
            (_acOnline && b.FullSinceUtc.HasValue && nowUtc >= b.FullSinceUtc.Value.AddMinutes(b.FullHoldMinutes)) ||
            (!_acOnline && !b.ReachedTarget && b.UnpluggedSinceUtc.HasValue && nowUtc >= b.UnpluggedSinceUtc.Value + PowerPolicy.BoostUnpluggedRevertDelay);

        if (due)
        {
            TriggerEcTask("冲高到期回落检查", nowUtc);
        }
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
            PersistModeState();
            _log.Info("KeepRunning expired; reverted to normal mode.");
        }

        return expired;
    }

    private void EnsureOriginalSnapshot()
    {
        if (File.Exists(_paths.OriginalJsonPath) || _dryRun)
        {
            return;
        }

        var snapshot = PowerSchemeInterop.CaptureOriginalSnapshot(DateTimeOffset.UtcNow);
        AtomicFileStore.WriteJsonAtomic(_paths.OriginalJsonPath, snapshot);
        _log.Info("Captured Windows original power settings snapshot.");
    }

    private void ApplyCurrentPowerScheme(string reason)
    {
        var target = PowerModeCalculator.GetTargetPowerScheme(_modeState.ParsedMode, _forcedSleep is not null);
        if (_dryRun)
        {
            _log.Info($"[DRY-RUN] ApplySettings({reason}): ac={target.AcDisplaySec}/{target.AcSleepSec}s dc={target.DcDisplaySec}/{target.DcSleepSec}s");
            return;
        }

        var changed = PowerSchemeInterop.ApplySettings(target);
        if (changed.Count > 0)
        {
            _log.Info($"Applied scheme ({reason}): {string.Join(", ", changed)}");
        }
    }

    private void ApplyCurrentWindowsPowerMode(string reason)
    {
        var target = PowerModeCalculator.GetWindowsPowerMode(_acOnline, _batteryPercent);
        if (_dryRun)
        {
            _log.Info($"[DRY-RUN] SetUserPowerMode({reason}): ac={_acOnline} -> {target}");
            return;
        }

        var current = PowerSchemeInterop.GetUserPowerMode(_acOnline);
        if (current != target)
        {
            PowerSchemeInterop.SetUserPowerMode(_acOnline, target);
            _log.Info($"Switched {(_acOnline ? "AC" : "DC")} power mode -> {target} ({reason})");
        }
    }

    private void RestoreOriginalSettingsAndStop()
    {
        var original = AtomicFileStore.ReadJsonOrDefault<OriginalPowerSettings>(_paths.OriginalJsonPath);
        if (original is not null && !_dryRun)
        {
            var restoreScheme = new PowerSchemeSettings(
                original.Ac.Display, original.Ac.Sleep,
                original.Dc.Display, original.Dc.Sleep,
                original.Ac.Lid, original.Dc.Lid,
                original.Ac.Button, original.Dc.Button);
            PowerSchemeInterop.ApplySettings(restoreScheme);
            _log.Info("Restored Windows original power settings.");
        }
        else
        {
            _log.Warn(_dryRun ? "[DRY-RUN] Skipped restoring original settings." : "original.json not found; skipping restore.");
        }

        _cts.Cancel();
    }

    private void PersistModeState()
    {
        if (!_dryRun)
        {
            AtomicFileStore.WriteJsonAtomic(_paths.ModeJsonPath, _modeState);
        }
    }

    private void TriggerEcTask(string reason, DateTimeOffset nowUtc)
    {
        if (_dryRun)
        {
            _log.Info($"[DRY-RUN] Would trigger {IpcConstants.BatteryChargeTaskName} (reason=\"{reason}\")");
            return;
        }

        AtomicFileStore.WriteEcRequestAtomic(_paths, new EcTaskRequest(EcTaskRequestKind.Trigger, reason, null, nowUtc));
        var (started, err) = ScheduledTaskTrigger.TriggerTask(IpcConstants.BatteryChargeTaskName);
        if (!started)
        {
            _log.Warn($"Failed to trigger {IpcConstants.BatteryChargeTaskName}: {err}");
        }
    }

    private void SendToastSafe(string title, string body)
    {
        if (_dryRun)
        {
            _log.Info($"[DRY-RUN] Toast: {title} | {body}");
            return;
        }

        try
        {
            ToastNotifierHelper.ShowToast(title, body);
        }
        catch (Exception ex)
        {
            _errorLog.Error($"Toast 发送失败：{ex}");
        }
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
