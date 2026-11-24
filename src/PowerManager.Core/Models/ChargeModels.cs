using System.Text.Json.Serialization;
using PowerManager.Core.Policy;

namespace PowerManager.Core.Models;

public sealed record ChargeThresholds(int Start, int Stop)
{
    public static ChargeThresholds Default { get; } =
        new(PowerPolicy.DefaultChargeStartPercent, PowerPolicy.DefaultChargeStopPercent);
}

public sealed record BoostRequest(
    [property: JsonPropertyName("target_stop")] int TargetStop,
    [property: JsonPropertyName("hold_minutes")] int HoldMinutes,
    [property: JsonPropertyName("max_hours")] double MaxHours)
{
    public bool TryValidate(out string? error)
    {
        if (TargetStop is < PowerPolicy.BoostTargetMinPercent or > PowerPolicy.BoostTargetMaxPercent)
        {
            error = $"target_stop 必须在 {PowerPolicy.BoostTargetMinPercent}–{PowerPolicy.BoostTargetMaxPercent} 之间（收到 {TargetStop}）。";
            return false;
        }

        if (HoldMinutes is < PowerPolicy.BoostHoldMinutesMin or > PowerPolicy.BoostHoldMinutesMax)
        {
            error = $"hold_minutes 必须在 {PowerPolicy.BoostHoldMinutesMin}–{PowerPolicy.BoostHoldMinutesMax} 之间（收到 {HoldMinutes}）。";
            return false;
        }

        if (double.IsNaN(MaxHours) || double.IsInfinity(MaxHours) ||
            MaxHours < PowerPolicy.BoostMaxHoursMin || MaxHours > PowerPolicy.BoostMaxHoursMax)
        {
            error = $"max_hours 必须在 {PowerPolicy.BoostMaxHoursMin}–{PowerPolicy.BoostMaxHoursMax} 之间（收到 {MaxHours}）。";
            return false;
        }

        error = null;
        return true;
    }
}

public sealed record BoostState(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("target_start")] int TargetStart,
    [property: JsonPropertyName("target_stop")] int TargetStop,
    [property: JsonPropertyName("full_hold_minutes")] int FullHoldMinutes,
    [property: JsonPropertyName("max_total_hours")] double MaxTotalHours,
    [property: JsonPropertyName("started_at_utc")] DateTimeOffset? StartedAtUtc,
    [property: JsonPropertyName("deadline_utc")] DateTimeOffset? DeadlineUtc,
    [property: JsonPropertyName("full_since_utc")] DateTimeOffset? FullSinceUtc,
    [property: JsonPropertyName("reached_target")] bool ReachedTarget,
    [property: JsonPropertyName("unplugged_since_utc")] DateTimeOffset? UnpluggedSinceUtc,
    [property: JsonPropertyName("reverted_at_utc")] DateTimeOffset? RevertedAtUtc,
    [property: JsonPropertyName("last_reason")] string? LastReason)
{
    public static BoostState Inactive { get; } = new(
        Active: false,
        TargetStart: PowerPolicy.DefaultChargeStartPercent,
        TargetStop: PowerPolicy.DefaultChargeStopPercent,
        FullHoldMinutes: PowerPolicy.BoostHoldMinutesDefault,
        MaxTotalHours: PowerPolicy.BoostMaxHoursDefault,
        StartedAtUtc: null,
        DeadlineUtc: null,
        FullSinceUtc: null,
        ReachedTarget: false,
        UnpluggedSinceUtc: null,
        RevertedAtUtc: null,
        LastReason: null);
}

public sealed record ChargeDecision(
    ChargeThresholds Target,
    BoostState NextBoost,
    bool Started,
    bool Reverted,
    string Reason);

public sealed record EcTelemetry(
    [property: JsonPropertyName("hw_ok")] bool HwOk,
    [property: JsonPropertyName("hw_start")] int? HwStart,
    [property: JsonPropertyName("hw_stop")] int? HwStop,
    [property: JsonPropertyName("charge_mode_raw")] int[]? ChargeModeRaw,
    [property: JsonPropertyName("resume_flag_raw")] int? ResumeFlagRaw,
    [property: JsonPropertyName("charge_ma")] int? ChargeMa,
    [property: JsonPropertyName("system_w")] int? SystemW,
    [property: JsonPropertyName("battery_c")] double? BatteryC,
    [property: JsonPropertyName("sensors_c")] Dictionary<string, double>? SensorsC,
    [property: JsonPropertyName("hottest_c")] double? HottestC);

public sealed record OsPowerSnapshot(
    [property: JsonPropertyName("ac_online")] bool AcOnline,
    [property: JsonPropertyName("percent")] int Percent);

public sealed record WindowsBatteryTelemetry(
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("charging")] bool Charging,
    [property: JsonPropertyName("charge_mw")] int ChargeMw,
    [property: JsonPropertyName("discharge_mw")] int DischargeMw,
    [property: JsonPropertyName("remaining_mwh")] int RemainingMwh,
    [property: JsonPropertyName("full_mwh")] int FullMwh);

public sealed record ChargeStateSnapshot(
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("ec")] EcTelemetry Ec,
    [property: JsonPropertyName("os_power")] OsPowerSnapshot OsPower,
    [property: JsonPropertyName("boost")] BoostState Boost,
    [property: JsonPropertyName("target")] int[] Target,
    [property: JsonPropertyName("win")] WindowsBatteryTelemetry? Win,
    [property: JsonPropertyName("run_reason")] string? RunReason);
