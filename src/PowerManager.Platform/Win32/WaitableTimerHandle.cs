using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PowerManager.Platform.Win32;

/// <summary>
/// 不唤醒系统的单一 Waitable Timer 封装（按 UTC 墙上时间设点，供 MsgWaitForMultipleObjectsEx 阻塞等待）。
/// </summary>
public sealed class WaitableTimerHandle : IDisposable
{
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private IntPtr _handle;
    private DateTimeOffset? _scheduledTargetUtc;

    public IntPtr RawHandle => _handle;
    public DateTimeOffset? ScheduledTargetUtc => _scheduledTargetUtc;

    public WaitableTimerHandle()
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
        if (_handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWaitableTimerExW 失败");
        }
    }

    /// <summary>
    /// 设定至指定的 UTC 墙上时间（不唤醒系统 fResume=false）。
    /// </summary>
    public void SetDueTimeUtc(DateTimeOffset dueAtUtc)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

        long fileTime = dueAtUtc.ToUniversalTime().ToFileTime();
        if (!SetWaitableTimer(_handle, ref fileTime, 0, IntPtr.Zero, IntPtr.Zero, fResume: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWaitableTimer 失败");
        }

        _scheduledTargetUtc = dueAtUtc.ToUniversalTime();
    }

    public void Cancel()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        CancelWaitableTimer(_handle);
        _scheduledTargetUtc = null;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
            _scheduledTargetUtc = null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(IntPtr hTimer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
