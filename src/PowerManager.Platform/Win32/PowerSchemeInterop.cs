using System.ComponentModel;
using System.Runtime.InteropServices;
using PowerManager.Core.Models;

namespace PowerManager.Platform.Win32;

/// <summary>
/// 通过 powrprof.dll / kernel32.dll 读写 Windows 电源方案、电源模式、电池快照与休眠（无需管理员）。
/// 写入超时后强制调用 PowerSetActiveScheme 刷新；调用 SetSuspendState(Hibernate) 前强制启用 SeShutdownPrivilege。
/// </summary>
public static partial class PowerSchemeInterop
{
    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");

    private static readonly Guid SettingVideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SettingStandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid SettingLidAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    private static readonly Guid SettingButtonAction = new("7648efa3-dd9c-4e3e-b566-50f929386280");

    private static readonly Guid ModeEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid ModeBalanced = Guid.Empty;
    private static readonly Guid ModePerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static PowerSchemeSettings ReadCurrentSettings()
    {
        Guid scheme = GetActiveScheme();
        return new PowerSchemeSettings(
            AcDisplaySec: (int)ReadValueIndex(true, scheme, SubVideo, SettingVideoIdle),
            AcSleepSec: (int)ReadValueIndex(true, scheme, SubSleep, SettingStandbyIdle),
            DcDisplaySec: (int)ReadValueIndex(false, scheme, SubVideo, SettingVideoIdle),
            DcSleepSec: (int)ReadValueIndex(false, scheme, SubSleep, SettingStandbyIdle),
            AcLidAction: (int)ReadValueIndex(true, scheme, SubButtons, SettingLidAction),
            DcLidAction: (int)ReadValueIndex(false, scheme, SubButtons, SettingLidAction),
            AcButtonAction: (int)ReadValueIndex(true, scheme, SubButtons, SettingButtonAction),
            DcButtonAction: (int)ReadValueIndex(false, scheme, SubButtons, SettingButtonAction));
    }

    public static OriginalPowerSettings CaptureOriginalSnapshot(DateTimeOffset nowUtc)
    {
        var cur = ReadCurrentSettings();
        return new OriginalPowerSettings(
            Ac: new OriginalPowerBucket(cur.AcDisplaySec, cur.AcSleepSec, cur.AcLidAction, cur.AcButtonAction),
            Dc: new OriginalPowerBucket(cur.DcDisplaySec, cur.DcSleepSec, cur.DcLidAction, cur.DcButtonAction),
            SavedAtUtc: nowUtc);
    }

    /// <summary>
    /// 仅写入与当前值不同的电源方案项；如有任何写入，强制调用 PowerSetActiveScheme 刷新生效。
    /// </summary>
    public static IReadOnlyList<string> ApplySettings(PowerSchemeSettings target)
    {
        var current = ReadCurrentSettings();
        Guid scheme = GetActiveScheme();
        var changed = new List<string>();

        WriteIfDifferent(true, "ac.display", current.AcDisplaySec, target.AcDisplaySec, scheme, SubVideo, SettingVideoIdle, changed);
        WriteIfDifferent(true, "ac.sleep", current.AcSleepSec, target.AcSleepSec, scheme, SubSleep, SettingStandbyIdle, changed);
        WriteIfDifferent(true, "ac.lid", current.AcLidAction, target.AcLidAction, scheme, SubButtons, SettingLidAction, changed);
        WriteIfDifferent(true, "ac.button", current.AcButtonAction, target.AcButtonAction, scheme, SubButtons, SettingButtonAction, changed);

        WriteIfDifferent(false, "dc.display", current.DcDisplaySec, target.DcDisplaySec, scheme, SubVideo, SettingVideoIdle, changed);
        WriteIfDifferent(false, "dc.sleep", current.DcSleepSec, target.DcSleepSec, scheme, SubSleep, SettingStandbyIdle, changed);
        WriteIfDifferent(false, "dc.lid", current.DcLidAction, target.DcLidAction, scheme, SubButtons, SettingLidAction, changed);
        WriteIfDifferent(false, "dc.button", current.DcButtonAction, target.DcButtonAction, scheme, SubButtons, SettingButtonAction, changed);

        if (changed.Count > 0)
        {
            uint rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (rc != 0)
            {
                throw new Win32Exception((int)rc, "PowerSetActiveScheme 刷新失败");
            }
        }

        return changed;
    }

    public static WindowsPowerMode? GetUserPowerMode(bool ac)
    {
        uint rc = ac
            ? PowerGetUserConfiguredACPowerMode(out Guid guid)
            : PowerGetUserConfiguredDCPowerMode(out guid);
        if (rc != 0)
        {
            return null;
        }

        if (guid == ModeEfficiency) return WindowsPowerMode.BestPowerEfficiency;
        if (guid == ModeBalanced) return WindowsPowerMode.Balanced;
        if (guid == ModePerformance) return WindowsPowerMode.BestPerformance;
        return null;
    }

    public static void SetUserPowerMode(bool ac, WindowsPowerMode mode)
    {
        Guid guid = mode switch
        {
            WindowsPowerMode.BestPowerEfficiency => ModeEfficiency,
            WindowsPowerMode.Balanced => ModeBalanced,
            WindowsPowerMode.BestPerformance => ModePerformance,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        uint rc = ac
            ? PowerSetUserConfiguredACPowerMode(ref guid)
            : PowerSetUserConfiguredDCPowerMode(ref guid);
        if (rc != 0)
        {
            throw new Win32Exception((int)rc, $"PowerSetUserConfigured{(ac ? "AC" : "DC")}PowerMode({mode}) 失败");
        }
    }

    /// <summary>
    /// 启用当前进程令牌的 SeShutdownPrivilege 后调用 SetSuspendState(Hibernate=true)。
    /// 返回 false 表示 Windows 拒绝本次休眠请求。
    /// </summary>
    public static bool TryHibernate()
    {
        EnableShutdownPrivilege();
        return SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false);
    }

    public static (bool? AcOnline, int? BatteryPercent) GetOsPowerStatus()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return (null, null);
        }

        bool? ac = status.ACLineStatus switch
        {
            1 => true,
            0 => false,
            _ => null
        };
        int? percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
        return (ac, percent);
    }

    private static Guid GetActiveScheme()
    {
        uint rc = PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid);
        if (rc != 0 || pGuid == IntPtr.Zero)
        {
            throw new Win32Exception((int)rc, "PowerGetActiveScheme 失败");
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pGuid);
        }
        finally
        {
            LocalFree(pGuid);
        }
    }

    private static uint ReadValueIndex(bool ac, Guid scheme, Guid subgroup, Guid setting)
    {
        uint rc = ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);
        if (rc != 0)
        {
            throw new Win32Exception((int)rc, $"PowerRead{(ac ? "AC" : "DC")}ValueIndex 失败");
        }

        return value;
    }

    private static void WriteIfDifferent(
        bool ac,
        string label,
        int currentVal,
        int targetVal,
        Guid scheme,
        Guid subgroup,
        Guid setting,
        List<string> changed)
    {
        if (currentVal == targetVal)
        {
            return;
        }

        uint rc = ac
            ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, (uint)targetVal)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, (uint)targetVal);
        if (rc != 0)
        {
            throw new Win32Exception((int)rc, $"PowerWrite{(ac ? "AC" : "DC")}ValueIndex({label}={targetVal}) 失败");
        }

        changed.Add($"{label}={targetVal}");
    }

    private static bool EnableShutdownPrivilege()
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;
        const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out LUID luid))
            {
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid, uint dcValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetUserConfiguredACPowerMode(out Guid powerModeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid powerModeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetUserConfiguredACPowerMode(ref Guid powerModeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid powerModeGuid);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
