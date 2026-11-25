using System.Text.Json.Serialization;

namespace PowerManager.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentMode
{
    Normal,
    KeepRunning
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WindowsPowerMode
{
    BestPowerEfficiency,
    Balanced,
    BestPerformance
}

/// <summary>
/// 写入 Windows 电源方案的完整目标超时与按键动作集合。
/// </summary>
public sealed record PowerSchemeSettings(
    int AcDisplaySec,
    int AcSleepSec,
    int DcDisplaySec,
    int DcSleepSec,
    int AcLidAction,
    int DcLidAction,
    int AcButtonAction,
    int DcButtonAction);

/// <summary>
/// 与 v1 agent/var/original.json 完全兼容的原始 Windows 电源设置快照。
/// </summary>
public sealed record OriginalPowerBucket(
    [property: JsonPropertyName("display")] int Display,
    [property: JsonPropertyName("sleep")] int Sleep,
    [property: JsonPropertyName("lid")] int Lid,
    [property: JsonPropertyName("button")] int Button);

public sealed record OriginalPowerSettings(
    [property: JsonPropertyName("ac")] OriginalPowerBucket Ac,
    [property: JsonPropertyName("dc")] OriginalPowerBucket Dc,
    [property: JsonPropertyName("saved_at_utc")] DateTimeOffset SavedAtUtc);

/// <summary>
/// 持久化于 &lt;DataDir&gt;/agent/mode.json 的模式状态（兼容 v1 字段名）。
/// </summary>
public sealed record ModeState(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("run_until_utc")] DateTimeOffset? RunUntilUtc,
    [property: JsonPropertyName("changed_at_utc")] DateTimeOffset ChangedAtUtc)
{
    public const string ModeNormalValue = "normal";
    public const string ModeKeepRunningValue = "keep_running";

    public AgentMode ParsedMode =>
        string.Equals(Mode, ModeKeepRunningValue, StringComparison.OrdinalIgnoreCase)
            ? AgentMode.KeepRunning
            : AgentMode.Normal;

    public static ModeState CreateNormal(DateTimeOffset nowUtc) =>
        new(ModeNormalValue, null, nowUtc);

    public static ModeState CreateKeepRunning(DateTimeOffset nowUtc, int durationHours) =>
        new(ModeKeepRunningValue, durationHours <= 0 ? null : nowUtc.AddHours(durationHours), nowUtc);
}
