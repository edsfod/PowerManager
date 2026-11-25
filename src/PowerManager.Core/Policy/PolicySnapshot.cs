using System.Text.Json.Serialization;

namespace PowerManager.Core.Policy;

/// <summary>
/// 下发给界面的策略数值（get_state 的 policy 字段）：前端滑条范围与规则说明文案中的数字都取自这里，
/// 不在前端手写。时长统一以秒为单位（0 表示从不），由前端 fmtDur 格式化。
/// 离线预览 web/mock.js 中的 policy 副本由 UiPolicyConsistencyTests 校验与本类一致。
/// </summary>
public sealed record PolicySnapshot(
    [property: JsonPropertyName("run_hours_min")] int RunHoursMin,
    [property: JsonPropertyName("run_hours_max")] int RunHoursMax,
    [property: JsonPropertyName("run_hours_default")] int RunHoursDefault,
    [property: JsonPropertyName("boost_target_min")] int BoostTargetMin,
    [property: JsonPropertyName("boost_target_max")] int BoostTargetMax,
    [property: JsonPropertyName("boost_hold_min")] int BoostHoldMin,
    [property: JsonPropertyName("boost_hold_max")] int BoostHoldMax,
    [property: JsonPropertyName("boost_maxh_min")] double BoostMaxHoursMin,
    [property: JsonPropertyName("boost_maxh_max")] double BoostMaxHoursMax,
    [property: JsonPropertyName("boost_hysteresis")] int BoostHysteresis,
    [property: JsonPropertyName("charge_start")] int ChargeStart,
    [property: JsonPropertyName("charge_stop")] int ChargeStop,
    [property: JsonPropertyName("normal_ac_display_sec")] int NormalAcDisplaySec,
    [property: JsonPropertyName("normal_ac_sleep_sec")] int NormalAcSleepSec,
    [property: JsonPropertyName("normal_dc_display_sec")] int NormalDcDisplaySec,
    [property: JsonPropertyName("normal_dc_sleep_sec")] int NormalDcSleepSec,
    [property: JsonPropertyName("run_ac_display_sec")] int RunAcDisplaySec,
    [property: JsonPropertyName("run_ac_sleep_sec")] int RunAcSleepSec,
    [property: JsonPropertyName("run_dc_display_sec")] int RunDcDisplaySec,
    [property: JsonPropertyName("run_dc_sleep_sec")] int RunDcSleepSec,
    [property: JsonPropertyName("lid_sleep_delay_sec")] int LidSleepDelaySec,
    [property: JsonPropertyName("hibernate_fallback_sec")] int HibernateFallbackSec,
    [property: JsonPropertyName("commute_temp_c")] double CommuteTempC,
    [property: JsonPropertyName("dc_balanced_min_pct")] int DcBalancedMinPct,
    [property: JsonPropertyName("history_step_pct")] int HistoryStepPct)
{
    public static PolicySnapshot Current { get; } = new(
        RunHoursMin: PowerPolicy.KeepRunningMinHours,
        RunHoursMax: PowerPolicy.KeepRunningMaxHours,
        RunHoursDefault: PowerPolicy.KeepRunningDefaultHours,
        BoostTargetMin: PowerPolicy.BoostTargetMinPercent,
        BoostTargetMax: PowerPolicy.BoostTargetMaxPercent,
        BoostHoldMin: PowerPolicy.BoostHoldMinutesMin,
        BoostHoldMax: PowerPolicy.BoostHoldMinutesMax,
        BoostMaxHoursMin: PowerPolicy.BoostMaxHoursMin,
        BoostMaxHoursMax: PowerPolicy.BoostMaxHoursMax,
        BoostHysteresis: PowerPolicy.BoostHysteresisPercent,
        ChargeStart: PowerPolicy.DefaultChargeStartPercent,
        ChargeStop: PowerPolicy.DefaultChargeStopPercent,
        NormalAcDisplaySec: PowerPolicy.NormalAcDisplayTimeoutSec,
        NormalAcSleepSec: PowerPolicy.NormalAcSleepTimeoutSec,
        NormalDcDisplaySec: PowerPolicy.NormalDcDisplayTimeoutSec,
        NormalDcSleepSec: PowerPolicy.NormalDcSleepTimeoutSec,
        RunAcDisplaySec: PowerPolicy.KeepRunningAcDisplayTimeoutSec,
        RunAcSleepSec: PowerPolicy.KeepRunningAcSleepTimeoutSec,
        RunDcDisplaySec: PowerPolicy.KeepRunningDcDisplayTimeoutSec,
        RunDcSleepSec: PowerPolicy.KeepRunningDcSleepTimeoutSec,
        LidSleepDelaySec: (int)PowerPolicy.NormalLidCloseSleepDelay.TotalSeconds,
        HibernateFallbackSec: (int)PowerPolicy.HibernateFallbackDelay.TotalSeconds,
        CommuteTempC: PowerPolicy.CommuteBatteryTempThresholdC,
        DcBalancedMinPct: PowerPolicy.DcBalancedMinPercent,
        HistoryStepPct: PowerPolicy.BatteryHistoryStepPercent);
}
