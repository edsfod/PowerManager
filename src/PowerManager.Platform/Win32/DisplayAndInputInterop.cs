using System.Runtime.InteropServices;

namespace PowerManager.Platform.Win32;

/// <summary>
/// 外接活动显示器枚举（QueryDisplayConfig）与键鼠空闲时长（GetLastInputInfo）。
/// 内置屏 outputTechnology 白名单依据 docs/reference/windows-standby-and-toast.md §一。
/// </summary>
public static class DisplayAndInputInterop
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;

    // INTERNAL (0x80000000), LVDS (6), DISPLAYPORT_EMBEDDED (11), UDI_EMBEDDED (13)
    private static readonly HashSet<uint> InternalOutputTechnologies = [0x80000000u, 6u, 11u, 13u];

    public static bool IsInternalTechnology(uint outputTechnology) =>
        InternalOutputTechnologies.Contains(outputTechnology);

    public static bool HasExternalActiveDisplay()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
        {
            return false;
        }

        if (pathCount == 0)
        {
            return false;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new byte[64 * Math.Max(1u, modeCount)];

        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
        {
            return false;
        }

        for (int i = 0; i < pathCount; i++)
        {
            if (!IsInternalTechnology(paths[i].TargetOutputTechnology))
            {
                return true;
            }
        }

        return false;
    }

    public static TimeSpan GetUserIdleDuration()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii))
        {
            return TimeSpan.Zero;
        }

        uint elapsedMs = unchecked(GetTickCount() - lii.dwTime);
        return TimeSpan.FromMilliseconds(elapsedMs);
    }

    // DISPLAYCONFIG_PATH_INFO 总大小 72 字节；targetInfo.outputTechnology 位于偏移 36
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        [FieldOffset(36)]
        public uint TargetOutputTechnology;

        [FieldOffset(68)]
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] byte[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();
}
