using PowerManager.Core.Logic;
using PowerManager.Core.Models;
using PowerManager.Core.Policy;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.Agent;

/// <summary>
/// 负责离开/回来时段跟踪、后台延迟查询事件日志生成醒来汇总，以及锁屏暂存与解锁补发 Toast。
/// </summary>
internal sealed class WakeSummaryCoordinator
{
    private readonly RotatingUtcLogger _log;
    private readonly RotatingUtcLogger _errorLog;
    private readonly Action<string, string, string> _sendToast;
    private readonly CancellationToken _shutdownToken;
    private readonly List<string> _awayNotes = [];
    private readonly object _toastLock = new();

    private DateTimeOffset? _awayStartUtc;
    private int? _awayStartBattery;
    private (string Title, string Body)? _pendingUnlockToast;
    private bool _sessionLocked;

    public WakeSummaryCoordinator(
        RotatingUtcLogger log,
        RotatingUtcLogger errorLog,
        Action<string, string, string> sendToast,
        CancellationToken shutdownToken)
    {
        _log = log;
        _errorLog = errorLog;
        _sendToast = sendToast;
        _shutdownToken = shutdownToken;
    }

    public void AddNote(string note) => _awayNotes.Add(note);

    public void MarkAwayStartIfNeeded(DateTimeOffset nowUtc, int currentBatteryPercent)
    {
        if (_awayStartUtc is not null)
        {
            return;
        }

        _awayStartUtc = nowUtc;
        _awayStartBattery = currentBatteryPercent;
        _awayNotes.Clear();
    }

    public void OnSessionLocked()
    {
        lock (_toastLock)
        {
            _sessionLocked = true;
        }
    }

    public void OnSessionUnlocked()
    {
        (string Title, string Body)? pending;
        lock (_toastLock)
        {
            _sessionLocked = false;
            pending = _pendingUnlockToast;
            _pendingUnlockToast = null;
        }

        if (pending.HasValue)
        {
            _sendToast(pending.Value.Title, pending.Value.Body, "wake");
        }
    }

    public void TriggerWakeSummaryIfAway(DateTimeOffset nowUtc, int currentBatteryPercent)
    {
        if (_awayStartUtc is null)
        {
            return;
        }

        DateTimeOffset awayStart = _awayStartUtc.Value;
        int? startBat = _awayStartBattery;
        var notesSnapshot = _awayNotes.ToArray();

        _awayStartUtc = null;
        _awayStartBattery = null;
        _awayNotes.Clear();

        if (!WakeSummaryFormatter.ShouldNotify(awayStart, nowUtc))
        {
            return;
        }

        // 后台线程延迟 15 秒查询 wevtapi（docs/design.md“醒来通知”）
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PowerPolicy.WakeLogQueryDelay, _shutdownToken).ConfigureAwait(false);
                var (winStart, winEnd) = WakeSummaryFormatter.GetQueryWindow(awayStart, nowUtc);
                var events = EventLogReaderInterop.QueryPowerEvents(winStart, winEnd);
                var summary = WakeSummaryFormatter.AnalyzeAndFormat(
                    new WakeSummaryInput(awayStart, nowUtc, startBat, currentBatteryPercent, notesSnapshot, events),
                    TimeZoneInfo.Local);

                _log.Info($"Wake summary: {summary.Title} | {summary.Body.Replace(Environment.NewLine, " / ")}");

                bool deferred;
                lock (_toastLock)
                {
                    if (_sessionLocked)
                    {
                        _pendingUnlockToast = (summary.Title, summary.Body);
                        deferred = true;
                    }
                    else
                    {
                        deferred = false;
                    }
                }

                if (!deferred)
                {
                    _sendToast(summary.Title, summary.Body, "wake");
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
}
