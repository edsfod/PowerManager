using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PowerManager.Platform.Win32;

public sealed record PowerSystemEvent(
    PowerSystemEventKind Kind,
    int Value);

public enum PowerSystemEventKind
{
    LidChanged,         // 0 = closed, 1 = open
    DisplayChanged,     // 0 = off, 1 = on, 2 = dimmed
    AcDcChanged,        // 0 = AC, 1 = DC, 2 = UPS
    BatteryPercent,     // 0..100
    SessionLocked,
    SessionUnlocked
}

/// <summary>
/// 隐藏顶级 Win32 窗口消息循环：监听合盖、屏幕、插拔电、电量每变 1% 与会话锁屏/解锁；
/// 遵循 docs/design.md §二.1，不监听 PBT_APMSUSPEND / PBT_APMRESUMEAUTOMATIC。
/// </summary>
public sealed class AgentMessageWindow : IDisposable
{
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const uint WM_QUIT = 0x0012;

    private const uint PBT_POWERSETTINGCHANGE = 0x8013;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    private const uint QS_ALLINPUT = 0x04FF;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint PM_REMOVE = 0x0001;

    private static readonly Guid GuidLidSwitchStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
    private static readonly Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid GuidAcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private static readonly Guid GuidBatteryPercentageRemaining = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");

    private readonly string _className = $"PowerManagerAgentWnd_{Guid.NewGuid():N}";
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly List<IntPtr> _powerNotifyHandles = [];
    private IntPtr _hwnd;
    private bool _wtsRegistered;
    private bool _disposed;

    public event Action<PowerSystemEvent>? SystemEventReceived;

    public AgentMessageWindow()
    {
        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = GetModuleHandleW(null),
            lpszClassName = _className
        };

        if (RegisterClassExW(ref wc) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW 失败");
        }

        // 必须创建隐藏的顶级窗口（非 HWND_MESSAGE），才能接收 WTS 与电源广播
        _hwnd = CreateWindowExW(0, _className, _className, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW 失败");
        }

        RegisterPowerGuid(GuidLidSwitchStateChange);
        RegisterPowerGuid(GuidConsoleDisplayState);
        RegisterPowerGuid(GuidAcDcPowerSource);
        RegisterPowerGuid(GuidBatteryPercentageRemaining);

        _wtsRegistered = WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);
    }

    /// <summary>
    /// 纯事件驱动阻塞等待：同时等待 Win32 消息、单一 Waitable Timer 与外部唤醒句柄（如 Named Pipe 命令队列）。
    /// </summary>
    public void RunEventLoop(
        WaitableTimerHandle timer,
        IntPtr externalSignalHandle,
        Action onTimerFired,
        Action onExternalSignal,
        CancellationToken cancellationToken)
    {
        var handles = new[] { timer.RawHandle, externalSignalHandle };

        while (!cancellationToken.IsCancellationRequested)
        {
            while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT)
                {
                    return;
                }

                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            uint result = MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, INFINITE, QS_ALLINPUT, 0);
            if (result == WAIT_OBJECT_0)
            {
                onTimerFired();
            }
            else if (result == WAIT_OBJECT_0 + 1)
            {
                onExternalSignal();
            }
        }
    }

    private void RegisterPowerGuid(Guid guid)
    {
        IntPtr h = RegisterPowerSettingNotification(_hwnd, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (h != IntPtr.Zero)
        {
            _powerNotifyHandles.Add(h);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_POWERBROADCAST && (uint)wParam == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.DataLength >= 4)
            {
                int value = Marshal.ReadInt32(lParam, Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)).ToInt32());
                if (setting.PowerSetting == GuidLidSwitchStateChange)
                {
                    SystemEventReceived?.Invoke(new PowerSystemEvent(PowerSystemEventKind.LidChanged, value));
                }
                else if (setting.PowerSetting == GuidConsoleDisplayState)
                {
                    SystemEventReceived?.Invoke(new PowerSystemEvent(PowerSystemEventKind.DisplayChanged, value));
                }
                else if (setting.PowerSetting == GuidAcDcPowerSource)
                {
                    SystemEventReceived?.Invoke(new PowerSystemEvent(PowerSystemEventKind.AcDcChanged, value));
                }
                else if (setting.PowerSetting == GuidBatteryPercentageRemaining)
                {
                    SystemEventReceived?.Invoke(new PowerSystemEvent(PowerSystemEventKind.BatteryPercent, value));
                }
            }
        }
        else if (msg == WM_WTSSESSION_CHANGE)
        {
            int state = (int)wParam;
            if (state == WTS_SESSION_LOCK)
            {
                SystemEventReceived?.Invoke(new PowerSystemEvent(PowerSystemEventKind.SessionLocked, 1));
            }
            else if (state == WTS_SESSION_UNLOCK)
            {
                SystemEventReceived?.Invoke(new PowerSystemEvent(PowerSystemEventKind.SessionUnlocked, 0));
            }
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IntPtr h in _powerNotifyHandles)
        {
            UnregisterPowerSettingNotification(h);
        }
        _powerNotifyHandles.Clear();

        if (_wtsRegistered && _hwnd != IntPtr.Zero)
        {
            WTSUnRegisterSessionNotification(_hwnd);
            _wtsRegistered = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClassW(_className, GetModuleHandleW(null));
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint nCount, [In] IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
}
