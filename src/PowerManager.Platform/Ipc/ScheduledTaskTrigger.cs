using System.Diagnostics;
using PowerManager.Core.Contracts;

namespace PowerManager.Platform.Ipc;

/// <summary>
/// 异步触发已注册的计划任务（如 Battery-ChargeLimit，不弹出 UAC、无控制台窗口）。
/// </summary>
public static class ScheduledTaskTrigger
{
    public static (bool Started, string? Error) TriggerTask(string taskName = IpcConstants.BatteryChargeTaskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{taskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "无法启动 schtasks.exe。");
            }

            if (!proc.WaitForExit(5000))
            {
                return (false, "触发计划任务超时。");
            }

            if (proc.ExitCode != 0)
            {
                string err = proc.StandardError.ReadToEnd().Trim();
                return (false, string.IsNullOrEmpty(err) ? $"schtasks 退出码 {proc.ExitCode}" : err);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
