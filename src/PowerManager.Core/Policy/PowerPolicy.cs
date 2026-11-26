namespace PowerManager.Core.Policy;

/// <summary>
/// 集中只读策略常量表：用户可感知的策略数值只在此定义（ARCHITECTURE.md 不变量 A6）。
/// 日常充电基准阈值 75%–80% 受 ADR-0003 防遗忘约束保护，仅在此定义，不暴露外部可编辑配置。
/// </summary>
public static class PowerPolicy
{
    // --- 日常充电阈值（CHG-01 / ADR-0003） ---
    public const int DefaultChargeStartPercent = 75;
    public const int DefaultChargeStopPercent = 80;

    // --- 临时冲高参数边界（CHG-02；界面快捷档位属于前端设计，定义在 web/app.js） ---
    public const int BoostTargetMinPercent = 85;
    public const int BoostTargetMaxPercent = 100;
    public const int BoostHysteresisPercent = 5;
    public const int BoostHoldMinutesMin = 15;
    public const int BoostHoldMinutesMax = 180;
    public const int BoostHoldMinutesDefault = 60;
    public const double BoostMaxHoursMin = 1.0;
    public const double BoostMaxHoursMax = 12.0;
    public const double BoostMaxHoursDefault = 4.0;
    public static readonly TimeSpan BoostUnpluggedRevertDelay = TimeSpan.FromMinutes(10);

    // --- 保持运行可选时长（SLP-02；0 表示直到手动改回） ---
    public const int KeepRunningIndefiniteHours = 0;
    public const int KeepRunningMinHours = 1;
    public const int KeepRunningMaxHours = 24;
    public const int KeepRunningDefaultHours = 12;

    // --- 双模式关屏与睡眠超时（秒，0 表示从不；SLP-03 / SLP-04 / SLP-05） ---
    public const int NormalAcDisplayTimeoutSec = 15 * 60;
    public const int NormalAcSleepTimeoutSec = 60 * 60;
    public const int NormalDcDisplayTimeoutSec = 5 * 60;
    public const int NormalDcSleepTimeoutSec = 15 * 60;

    public const int KeepRunningAcDisplayTimeoutSec = 15 * 60;
    public const int KeepRunningAcSleepTimeoutSec = 0; // 从不自动睡眠
    public const int KeepRunningDcDisplayTimeoutSec = 5 * 60;
    public const int KeepRunningDcSleepTimeoutSec = 30 * 60;

    // --- 主动诱导现代待机临时超时（ADR-0002 / SLP-06） ---
    public const int ForcedSleepDisplayTimeoutSec = 10;
    public const int ForcedSleepTimeoutSec = 15;

    // --- 合盖与电源键系统动作（0 = 不采取任何操作；SLP-06 / SLP-08） ---
    public const int DoNothingPowerAction = 0;

    // --- 电源模式电量切换阈值（PWR-02） ---
    public const int DcBalancedMinPercent = 50;

    // --- 合盖、通勤温控、防误伤与休眠兜底时间边界（SLP-05 / SLP-06 / SLP-10 / SLP-11） ---
    public static readonly TimeSpan NormalLidCloseSleepDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CommuteTempCheckInterval = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan CommuteTempReadDelayAfterTrigger = TimeSpan.FromSeconds(10);
    public const double CommuteBatteryTempThresholdC = 40.0;
    public static readonly TimeSpan CommuteTempStateMaxAge = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan CommuteTempActiveUserSkipIdle = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CommuteIdleTakeoverThreshold = TimeSpan.FromMinutes(32);

    public static readonly TimeSpan ForcedSleepCheckInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ForcedSleepActiveUserAbortIdle = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HibernateFallbackDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan TimerFrozenDriftThreshold = TimeSpan.FromSeconds(30);

    // --- 醒来通知解析窗口与阈值（NTF-01） ---
    public static readonly TimeSpan WakeNotifyMinAwayDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan WakeLogQueryDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan WakeLogWindowBeforeOff = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WakeLogWindowAfterOn = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan WakeSleepMinDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan WakeAwakeAttachmentMinDuration = TimeSpan.FromSeconds(120);

    // --- 历史日志与轮换（CHG-03） ---
    public const int BatteryHistoryStepPercent = 5;
    public const long MaxLogFileBytes = 1024 * 1024;
}
