using PowerManager.Core.Logic;

namespace PowerManager.Tests;

public sealed class WakeSummaryFormatterTests
{
    private static readonly TimeZoneInfo UtcPlus8 = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 2, 0, 0, TimeSpan.Zero); // 10:00 UTC+8

    [Fact]
    public void GetQueryWindow_AppliesMinus5sAndPlus15s()
    {
        var (start, end) = WakeSummaryFormatter.GetQueryWindow(T0, T0.AddMinutes(30));
        Assert.Equal(T0.AddSeconds(-5), start);
        Assert.Equal(T0.AddMinutes(30).AddSeconds(15), end);
    }

    [Fact]
    public void AnalyzeAndFormat_HibernateTakesPriorityEvenUnder60Seconds()
    {
        var events = new[]
        {
            new PowerLogEvent(PowerLogEventKind.KernelPower506, T0, Reason: 15),
            new PowerLogEvent(PowerLogEventKind.KernelPower506, T0.AddMinutes(2), Reason: 20), // 排除
            new PowerLogEvent(PowerLogEventKind.KernelPower507, T0.AddMinutes(2), Reason: 20, SleepEntered: false), // 排除
            new PowerLogEvent(
                PowerLogEventKind.PowerTroubleshooter1,
                T0.AddMinutes(10),
                TargetState: 5,
                SleepTimeUtc: T0.AddMinutes(9).AddSeconds(20),
                WakeTimeUtc: T0.AddMinutes(10)), // 40s 休眠也计为休眠
            new PowerLogEvent(PowerLogEventKind.KernelPower506, T0.AddMinutes(10), Reason: 25), // 排除
            new PowerLogEvent(PowerLogEventKind.KernelPower507, T0.AddMinutes(10).AddSeconds(1), Reason: 15)
        };

        var input = new WakeSummaryInput(
            AwayStartUtc: T0,
            AwayEndUtc: T0.AddMinutes(10),
            StartBatteryPercent: 80,
            EndBatteryPercent: 78,
            AgentNotes: ["合盖 1 分钟", "2 分钟未入睡转休眠"],
            Events: events);

        var result = WakeSummaryFormatter.AnalyzeAndFormat(input, UtcPlus8);
        Assert.Equal(WakeOutcomeKind.Hibernate, result.Outcome);
        Assert.Equal(0, result.BriefWakeCount); // Reason=20 已排除
        Assert.Equal("合盖 1 分钟", result.EnterReason);
        Assert.Equal("开盖", result.WakeReason);
        Assert.Contains("休眠 10 分钟（10:00–10:10）", result.Title);
        Assert.Contains("醒着共 9 分钟", result.Body); // 10m - 40s >= 120s
        Assert.Contains("电量 80%→78%（-2%）", result.Body);
        Assert.Contains("2 分钟未入睡转休眠", result.Body);
    }

    [Fact]
    public void AnalyzeAndFormat_SleepRequiresAtLeast60SecondsAndExcludesReason20()
    {
        var events = new[]
        {
            new PowerLogEvent(PowerLogEventKind.KernelPower506, T0, Reason: 12),
            new PowerLogEvent(PowerLogEventKind.KernelPower507, T0.AddMinutes(10), Reason: 28, SleepEntered: true, SleepDurationUs: 540_000_000), // 9m
            new PowerLogEvent(PowerLogEventKind.KernelPower507, T0.AddMinutes(20), Reason: 20, SleepEntered: false), // 排除
            new PowerLogEvent(PowerLogEventKind.KernelPower507, T0.AddMinutes(30), Reason: 15, SleepEntered: true, SleepDurationUs: 1_200_000_000) // 20m
        };

        var input = new WakeSummaryInput(
            AwayStartUtc: T0,
            AwayEndUtc: T0.AddMinutes(30),
            StartBatteryPercent: 75,
            EndBatteryPercent: 74,
            AgentNotes: [],
            Events: events);

        var result = WakeSummaryFormatter.AnalyzeAndFormat(input, UtcPlus8);
        Assert.Equal(WakeOutcomeKind.Sleep, result.Outcome);
        Assert.Equal(1, result.BriefWakeCount); // 2 个有效 507，减去最终唤醒 = 1 次短暂唤醒
        Assert.Equal("闲置超时", result.EnterReason);
        Assert.Equal("开盖", result.WakeReason);
        Assert.DoesNotContain("醒着共", result.Body); // 醒着 1 分钟 < 120s 不附加
    }

    [Fact]
    public void AnalyzeAndFormat_Under60SecondsSleepClassifiedAsAwake()
    {
        var events = new[]
        {
            new PowerLogEvent(PowerLogEventKind.KernelPower506, T0, Reason: 12),
            new PowerLogEvent(PowerLogEventKind.KernelPower507, T0.AddMinutes(5), Reason: 31, SleepEntered: true, SleepDurationUs: 59_000_000)
        };

        var input = new WakeSummaryInput(T0, T0.AddMinutes(5), 60, 58, [], events);
        var result = WakeSummaryFormatter.AnalyzeAndFormat(input, UtcPlus8);

        Assert.Equal(WakeOutcomeKind.Awake, result.Outcome);
        Assert.Contains("醒着 5 分钟", result.Title);
        Assert.DoesNotContain("醒着共", result.Body);
    }
}
