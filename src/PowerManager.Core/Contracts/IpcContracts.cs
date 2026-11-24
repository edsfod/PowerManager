using System.Text.Json.Serialization;
using PowerManager.Core.Models;

namespace PowerManager.Core.Contracts;

public static class IpcConstants
{
    public const string AgentPipeName = "PowerManager.Agent";
    public const string AppUserModelId = "PowerManager";
    public const string AgentTaskName = "PowerManager-Agent";
    public const string BatteryChargeTaskName = "Battery-ChargeLimit";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentIpcCommand
{
    Ping,
    GetStatus,
    SetMode,
    RestoreOriginal
}

public sealed record AgentIpcRequest(
    [property: JsonPropertyName("command")] AgentIpcCommand Command,
    [property: JsonPropertyName("mode")] AgentMode? Mode = null,
    [property: JsonPropertyName("duration_hours")] int? DurationHours = null);

public sealed record AgentIpcResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("mode_state")] ModeState? ModeState = null,
    [property: JsonPropertyName("forced_sleep_active")] bool ForcedSleepActive = false,
    [property: JsonPropertyName("data_dir")] string? DataDir = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EcTaskRequestKind
{
    Trigger,
    Boost
}

/// <summary>
/// 普通权限进程通过 &lt;DataDir&gt;/requests/*.json 投递给 S4U 提权任务的白名单请求契约。
/// </summary>
public sealed record EcTaskRequest(
    [property: JsonPropertyName("kind")] EcTaskRequestKind Kind,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("boost")] BoostRequest? Boost,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc)
{
    public const int MaxReasonLength = 120;

    public bool TryValidate(out string? error)
    {
        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > MaxReasonLength)
        {
            error = $"reason 不能为空且长度不得超过 {MaxReasonLength} 字符。";
            return false;
        }

        if (Kind == EcTaskRequestKind.Boost)
        {
            if (Boost is null)
            {
                error = "kind=Boost 时必须提供 boost 参数。";
                return false;
            }

            return Boost.TryValidate(out error);
        }

        if (Boost is not null)
        {
            error = "kind=Trigger 时不得携带 boost 参数。";
            return false;
        }

        error = null;
        return true;
    }
}
