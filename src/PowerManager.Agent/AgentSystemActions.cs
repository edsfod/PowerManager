using PowerManager.Core.Contracts;
using PowerManager.Core.Logic;
using PowerManager.Core.Models;
using PowerManager.Platform.Ipc;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.Agent;

/// <summary>
/// 封装常驻调度层的系统副作用操作（电源方案/模式写入、快照持久化、EC 任务触发、休眠与 Toast），统一处理 dry-run 与日志。
/// </summary>
internal sealed class AgentSystemActions
{
    private readonly AppPaths _paths;
    private readonly bool _dryRun;
    private readonly RotatingUtcLogger _log;
    private readonly RotatingUtcLogger _errorLog;

    public AgentSystemActions(AppPaths paths, bool dryRun, RotatingUtcLogger log, RotatingUtcLogger errorLog)
    {
        _paths = paths;
        _dryRun = dryRun;
        _log = log;
        _errorLog = errorLog;
    }

    public void EnsureOriginalSnapshot()
    {
        if (File.Exists(_paths.OriginalJsonPath) || _dryRun)
        {
            return;
        }

        var snapshot = PowerSchemeInterop.CaptureOriginalSnapshot(DateTimeOffset.UtcNow);
        AtomicFileStore.WriteJsonAtomic(_paths.OriginalJsonPath, snapshot);
        _log.Info("Captured Windows original power settings snapshot.");
    }

    public void ApplyPowerScheme(AgentMode mode, bool forcedSleepActive, string reason)
    {
        var target = PowerModeCalculator.GetTargetPowerScheme(mode, forcedSleepActive);
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

    public void ApplyWindowsPowerMode(bool acOnline, int batteryPercent, string reason)
    {
        var target = PowerModeCalculator.GetWindowsPowerMode(acOnline, batteryPercent);
        if (_dryRun)
        {
            _log.Info($"[DRY-RUN] SetUserPowerMode({reason}): ac={acOnline} -> {target}");
            return;
        }

        var current = PowerSchemeInterop.GetUserPowerMode(acOnline);
        if (current != target)
        {
            PowerSchemeInterop.SetUserPowerMode(acOnline, target);
            _log.Info($"Switched {(acOnline ? "AC" : "DC")} power mode -> {target} ({reason})");
        }
    }

    public void RestoreOriginalSettings()
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
    }

    public void PersistModeState(ModeState modeState)
    {
        if (!_dryRun)
        {
            AtomicFileStore.WriteJsonAtomic(_paths.ModeJsonPath, modeState);
        }
    }

    public void TriggerEcTask(string reason, DateTimeOffset nowUtc)
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

    public bool TryHibernate()
    {
        if (_dryRun)
        {
            _log.Info("[DRY-RUN] Skipped SetSuspendState(Hibernate).");
            return false;
        }

        if (!PowerSchemeInterop.TryHibernate())
        {
            _log.Warn("SetSuspendState(Hibernate) rejected by Windows; resetting timer for next retry.");
            return false;
        }

        return true;
    }

    public void SendToastSafe(string title, string body, string tag = "status")
    {
        if (_dryRun)
        {
            _log.Info($"[DRY-RUN] Toast (tag={tag}): {title} | {body}");
            return;
        }

        try
        {
            ToastNotifierHelper.ShowToast(title, body, tag: tag);
        }
        catch (Exception ex)
        {
            _errorLog.Error($"Toast 发送失败：{ex}");
        }
    }
}
