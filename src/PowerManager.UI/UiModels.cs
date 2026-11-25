using System.Text.Json.Serialization;
using PowerManager.Core.Policy;

namespace PowerManager.UI;

public sealed record UiPrefs(
    [property: JsonPropertyName("hours")] int Hours = PowerPolicy.KeepRunningDefaultHours,
    [property: JsonPropertyName("boost_target")] int BoostTarget = 95,
    [property: JsonPropertyName("boost_hold")] int BoostHold = PowerPolicy.BoostHoldMinutesDefault,
    [property: JsonPropertyName("boost_maxh")] double BoostMaxHours = PowerPolicy.BoostMaxHoursDefault,
    [property: JsonPropertyName("rules_expanded")] bool RulesExpanded = false);

public sealed record UiActionResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("msg")] string? Msg = null);

public sealed record UiModeView(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("hours")] int Hours,
    [property: JsonPropertyName("until")] string? Until,
    [property: JsonPropertyName("left")] string? Left,
    [property: JsonPropertyName("run_until_utc")] string? RunUntilUtc);

public sealed record UiPowerModeView(
    [property: JsonPropertyName("ac")] string Ac,
    [property: JsonPropertyName("dc")] string Dc);

public sealed record UiBatteryView(
    [property: JsonPropertyName("pct")] int? Pct,
    [property: JsonPropertyName("ac")] bool? Ac,
    [property: JsonPropertyName("flow")] string Flow,
    [property: JsonPropertyName("band")] int[] Band,
    [property: JsonPropertyName("hw")] int[]? Hw,
    [property: JsonPropertyName("temp")] double? Temp,
    [property: JsonPropertyName("system_w")] int? SystemW,
    [property: JsonPropertyName("charge_ma")] int? ChargeMa,
    [property: JsonPropertyName("hottest")] double? Hottest,
    [property: JsonPropertyName("read_at")] string? ReadAt,
    [property: JsonPropertyName("read_age_min")] int? ReadAgeMinutes,
    [property: JsonPropertyName("slow")] bool Slow,
    [property: JsonPropertyName("last")] string Last);

public sealed record UiBoostView(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("target")] int? Target = null,
    [property: JsonPropertyName("hold")] int? Hold = null,
    [property: JsonPropertyName("held")] int? Held = null,
    [property: JsonPropertyName("full_since_utc")] string? FullSinceUtc = null,
    [property: JsonPropertyName("deadline")] string? Deadline = null);

public sealed record UiBoostPrefsView(
    [property: JsonPropertyName("target")] int Target,
    [property: JsonPropertyName("hold")] int Hold,
    [property: JsonPropertyName("maxh")] double MaxHours);

public sealed record UiStateView(
    [property: JsonPropertyName("agent")] bool Agent,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("forced_sleep")] bool ForcedSleep,
    [property: JsonPropertyName("rules_expanded")] bool RulesExpanded,
    [property: JsonPropertyName("data_dir")] string DataDir,
    [property: JsonPropertyName("mode")] UiModeView Mode,
    [property: JsonPropertyName("power_mode")] UiPowerModeView PowerMode,
    [property: JsonPropertyName("battery")] UiBatteryView Battery,
    [property: JsonPropertyName("boost")] UiBoostView Boost,
    [property: JsonPropertyName("boost_prefs")] UiBoostPrefsView BoostPrefs);
