using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using PowerManager.Core.Logic;

namespace PowerManager.Platform.Win32;

/// <summary>
/// 通过 wevtapi.dll 以普通用户权限只读查询 System 日志中的 Kernel-Power (506/507/42) 与 Power-Troubleshooter (1)。
/// </summary>
public static class EventLogReaderInterop
{
    private const uint EvtQueryChannelPath = 0x1;
    private const uint EvtRenderEventXml = 1;

    public static IReadOnlyList<PowerLogEvent> QueryPowerEvents(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        string startIso = startUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string endIso = endUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        string xpath =
            $"*[System[TimeCreated[@SystemTime>='{startIso}' and @SystemTime<='{endIso}'] and " +
            "((Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=506 or EventID=507 or EventID=42)) or " +
            "(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1))]]";

        IntPtr queryHandle = EvtQuery(IntPtr.Zero, "System", xpath, EvtQueryChannelPath);
        if (queryHandle == IntPtr.Zero)
        {
            return [];
        }

        var results = new List<PowerLogEvent>();
        try
        {
            var eventHandles = new IntPtr[32];
            while (EvtNext(queryHandle, (uint)eventHandles.Length, eventHandles, 2000, 0, out uint returned) && returned > 0)
            {
                for (int i = 0; i < returned; i++)
                {
                    IntPtr hEvent = eventHandles[i];
                    try
                    {
                        string? xml = RenderEventXml(hEvent);
                        if (xml is not null && TryParseEventXml(xml, out var parsed) && parsed is not null)
                        {
                            results.Add(parsed);
                        }
                    }
                    finally
                    {
                        EvtClose(hEvent);
                    }
                }
            }
        }
        finally
        {
            EvtClose(queryHandle);
        }

        return results;
    }

    public static bool TryParseEventXml(string xml, out PowerLogEvent? parsed)
    {
        parsed = null;
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root is null)
        {
            return false;
        }

        XNamespace ns = root.Name.Namespace;
        var system = root.Element(ns + "System");
        if (system is null)
        {
            return false;
        }

        string? provider = system.Element(ns + "Provider")?.Attribute("Name")?.Value;
        if (!int.TryParse(system.Element(ns + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int eventId))
        {
            return false;
        }

        string? timeStr = system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
        if (!DateTimeOffset.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestampUtc))
        {
            return false;
        }

        var dataMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var eventData = root.Element(ns + "EventData");
        if (eventData is not null)
        {
            foreach (var d in eventData.Elements(ns + "Data"))
            {
                string? name = d.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    dataMap[name] = d.Value;
                }
            }
        }

        if (string.Equals(provider, "Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase))
        {
            switch (eventId)
            {
                case 506:
                    parsed = new PowerLogEvent(
                        PowerLogEventKind.KernelPower506,
                        timestampUtc,
                        Reason: TryParseInt(dataMap, "Reason"));
                    return true;

                case 507:
                    parsed = new PowerLogEvent(
                        PowerLogEventKind.KernelPower507,
                        timestampUtc,
                        Reason: TryParseInt(dataMap, "Reason"),
                        SleepEntered: TryParseBool(dataMap, "SleepEntered"),
                        SleepDurationUs: TryParseLong(dataMap, "SleepDurationInUs"));
                    return true;

                case 42:
                    parsed = new PowerLogEvent(
                        PowerLogEventKind.KernelPower42,
                        timestampUtc,
                        TargetState: TryParseInt(dataMap, "TargetState"));
                    return true;
            }
        }
        else if (string.Equals(provider, "Microsoft-Windows-Power-Troubleshooter", StringComparison.OrdinalIgnoreCase) && eventId == 1)
        {
            parsed = new PowerLogEvent(
                PowerLogEventKind.PowerTroubleshooter1,
                timestampUtc,
                TargetState: TryParseInt(dataMap, "TargetState"),
                SleepTimeUtc: TryParseDateTime(dataMap, "SleepTime"),
                WakeTimeUtc: TryParseDateTime(dataMap, "WakeTime"));
            return true;
        }

        return false;
    }

    private static string? RenderEventXml(IntPtr hEvent)
    {
        EvtRender(IntPtr.Zero, hEvent, EvtRenderEventXml, 0, IntPtr.Zero, out uint bufferUsed, out _);
        if (bufferUsed == 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferUsed);
        try
        {
            if (!EvtRender(IntPtr.Zero, hEvent, EvtRenderEventXml, bufferUsed, buffer, out _, out _))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? TryParseInt(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static long TryParseLong(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var raw) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;

    private static bool TryParseBool(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var raw) && bool.TryParse(raw, out bool v) && v;

    private static DateTimeOffset? TryParseDateTime(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var raw) &&
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset dt)
            ? dt
            : null;

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtQuery(IntPtr session, string path, string query, uint flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(
        IntPtr resultSet, uint eventArraySize, [Out] IntPtr[] eventArray, uint timeout, uint flags, out uint returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(
        IntPtr context, IntPtr fragment, uint flags, uint bufferSize, IntPtr buffer, out uint bufferUsed, out uint propertyCount);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr handle);
}
