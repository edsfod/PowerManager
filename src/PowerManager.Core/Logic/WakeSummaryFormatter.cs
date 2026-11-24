using System.Text;
using PowerManager.Core.Policy;

namespace PowerManager.Core.Logic;

public enum PowerLogEventKind
{
    KernelPower506,
    KernelPower507,
    KernelPower42,
    PowerTroubleshooter1
}

public sealed record PowerLogEvent(
    PowerLogEventKind Kind,
    DateTimeOffset TimestampUtc,
    int? Reason = null,
    bool SleepEntered = false,
    long SleepDurationUs = 0,
    int? TargetState = null,
    DateTimeOffset? SleepTimeUtc = null,
    DateTimeOffset? WakeTimeUtc = null);

public enum WakeOutcomeKind
{
    Awake,
    Sleep,
    Hibernate
}

public sealed record WakeSummaryInput(
    DateTimeOffset AwayStartUtc,
    DateTimeOffset AwayEndUtc,
    int? StartBatteryPercent,
    int? EndBatteryPercent,
    IReadOnlyList<string> AgentNotes,
    IReadOnlyList<PowerLogEvent> Events);

public sealed record WakeSummaryResult(
    WakeOutcomeKind Outcome,
    TimeSpan TotalAwayDuration,
    TimeSpan EffectiveRestDuration,
    TimeSpan AwakeDuration,
    int BriefWakeCount,
    string? EnterReason,
    string? WakeReason,
    string Title,
    string Body);

/// <summary>
/// 醒来事件日志解析与 Toast 摘要拼装纯函数（NTF-01 / docs/design.md §二.3）。
/// </summary>
public static class WakeSummaryFormatter
{
    private const int HibernateTargetState = 5;
    private const int ReasonTransitionToHibernateOrShutdown = 20;
    private const int ReasonResumeFromHibernate = 25;

    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetQueryWindow(
        DateTimeOffset awayStartUtc,
        DateTimeOffset awayEndUtc) =>
        (awayStartUtc - PowerPolicy.WakeLogWindowBeforeOff, awayEndUtc + PowerPolicy.WakeLogWindowAfterOn);

    public static bool ShouldNotify(DateTimeOffset awayStartUtc, DateTimeOffset awayEndUtc) =>
        (awayEndUtc - awayStartUtc) >= PowerPolicy.WakeNotifyMinAwayDuration;

    public static WakeSummaryResult AnalyzeAndFormat(WakeSummaryInput input, TimeZoneInfo localTimeZone)
    {
        TimeSpan totalAway = input.AwayEndUtc > input.AwayStartUtc
            ? input.AwayEndUtc - input.AwayStartUtc
            : TimeSpan.Zero;

        var orderedEvents = input.Events.OrderBy(e => e.TimestampUtc).ToList();

        // 506 排除 Reason=20/25；507 排除 Reason=20
        var valid506 = orderedEvents
            .Where(e => e.Kind == PowerLogEventKind.KernelPower506 &&
                        e.Reason != ReasonTransitionToHibernateOrShutdown &&
                        e.Reason != ReasonResumeFromHibernate)
            .ToList();

        var valid507 = orderedEvents
            .Where(e => e.Kind == PowerLogEventKind.KernelPower507 &&
                        e.Reason != ReasonTransitionToHibernateOrShutdown)
            .ToList();

        var hibernateEvents = orderedEvents
            .Where(e => e.Kind == PowerLogEventKind.PowerTroubleshooter1 &&
                        e.TargetState == HibernateTargetState)
            .ToList();

        // 真实睡眠累计（507 且 SleepEntered=true）
        long totalSleepUs = orderedEvents
            .Where(e => e.Kind == PowerLogEventKind.KernelPower507 && e.SleepEntered && e.SleepDurationUs > 0)
            .Sum(e => e.SleepDurationUs);
        TimeSpan sleepDuration = TimeSpan.FromTicks(totalSleepUs * 10);

        // 休眠累计（Power-Troubleshooter 1, TargetState=5）
        TimeSpan hibernateDuration = TimeSpan.Zero;
        foreach (var h in hibernateEvents)
        {
            if (h.SleepTimeUtc.HasValue && h.WakeTimeUtc.HasValue && h.WakeTimeUtc.Value > h.SleepTimeUtc.Value)
            {
                hibernateDuration += h.WakeTimeUtc.Value - h.SleepTimeUtc.Value;
            }
        }

        // 状态判定优先级：有休眠记录 > 真实睡眠 ≥ 60 秒 > 醒着
        WakeOutcomeKind outcome;
        TimeSpan effectiveRest;
        if (hibernateEvents.Count > 0)
        {
            outcome = WakeOutcomeKind.Hibernate;
            effectiveRest = hibernateDuration + sleepDuration;
            if (effectiveRest > totalAway)
            {
                effectiveRest = totalAway;
            }
        }
        else if (sleepDuration >= PowerPolicy.WakeSleepMinDuration)
        {
            outcome = WakeOutcomeKind.Sleep;
            effectiveRest = sleepDuration > totalAway ? totalAway : sleepDuration;
        }
        else
        {
            outcome = WakeOutcomeKind.Awake;
            effectiveRest = TimeSpan.Zero;
        }

        TimeSpan awakeDuration = totalAway > effectiveRest ? totalAway - effectiveRest : TimeSpan.Zero;
        int briefWakeCount = Math.Max(0, valid507.Count - 1);

        // 入睡原因：常驻 notes 首条覆盖入睡原因，后续备注附在第二行
        string? enterReason = input.AgentNotes.Count > 0
            ? input.AgentNotes[0]
            : (valid506.Count > 0 ? MapEnterReason(valid506[0].Reason) : null);

        string? wakeReason = valid507.Count > 0
            ? MapWakeReason(valid507[^1].Reason)
            : null;

        string statusLabel = outcome switch
        {
            WakeOutcomeKind.Hibernate => "休眠",
            WakeOutcomeKind.Sleep => "睡眠",
            _ => "醒着"
        };

        var localStart = TimeZoneInfo.ConvertTime(input.AwayStartUtc, localTimeZone);
        var localEnd = TimeZoneInfo.ConvertTime(input.AwayEndUtc, localTimeZone);
        string title = $"{statusLabel} {FormatDuration(totalAway)}（{localStart:HH:mm}–{localEnd:HH:mm}）";

        var line1Parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(enterReason))
        {
            line1Parts.Add($"入睡：{enterReason}");
        }
        if (!string.IsNullOrWhiteSpace(wakeReason))
        {
            line1Parts.Add($"唤醒：{wakeReason}");
        }
        if (briefWakeCount > 0)
        {
            line1Parts.Add($"短暂唤醒 {briefWakeCount} 次");
        }
        if (outcome != WakeOutcomeKind.Awake && awakeDuration >= PowerPolicy.WakeAwakeAttachmentMinDuration)
        {
            int awakeMinutes = (int)Math.Round(awakeDuration.TotalMinutes);
            line1Parts.Add($"醒着共 {awakeMinutes} 分钟");
        }
        if (input.StartBatteryPercent.HasValue && input.EndBatteryPercent.HasValue)
        {
            int diff = input.EndBatteryPercent.Value - input.StartBatteryPercent.Value;
            string sign = diff > 0 ? "+" : string.Empty;
            line1Parts.Add($"电量 {input.StartBatteryPercent.Value}%→{input.EndBatteryPercent.Value}%（{sign}{diff}%）");
        }

        var bodyBuilder = new StringBuilder();
        bodyBuilder.Append(string.Join(" · ", line1Parts));

        if (input.AgentNotes.Count > 1)
        {
            if (bodyBuilder.Length > 0)
            {
                bodyBuilder.AppendLine();
            }

            bodyBuilder.Append(string.Join("；", input.AgentNotes.Skip(1)));
        }

        return new WakeSummaryResult(
            Outcome: outcome,
            TotalAwayDuration: totalAway,
            EffectiveRestDuration: effectiveRest,
            AwakeDuration: awakeDuration,
            BriefWakeCount: briefWakeCount,
            EnterReason: enterReason,
            WakeReason: wakeReason,
            Title: title,
            Body: bodyBuilder.ToString());
    }

    public static string? MapEnterReason(int? reason) => reason switch
    {
        1 => "电源键",
        11 => "程序关屏",
        12 => "闲置超时",
        15 => "合盖",
        28 => "插拔电",
        31 => "键盘",
        32 => "鼠标",
        55 => "待机耗电超预算",
        null => null,
        _ => $"代码 {reason.Value}"
    };

    public static string? MapWakeReason(int? reason) => reason switch
    {
        1 => "电源键",
        15 => "开盖",
        28 => "插拔电",
        31 => "键盘",
        32 => "鼠标",
        55 => "待机耗电超预算",
        null => null,
        _ => $"代码 {reason.Value}"
    };

    private static string FormatDuration(TimeSpan duration)
    {
        int totalMinutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return hours > 0
            ? (minutes > 0 ? $"{hours} 小时 {minutes} 分钟" : $"{hours} 小时")
            : $"{minutes} 分钟";
    }
}
