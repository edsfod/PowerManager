namespace PowerManager.Core.Policy;

/// <summary>
/// 把策略时长格式化为面向用户的中文文案，供通知与备注引用 PowerPolicy 而非手写数字。
/// 规则与前端 web/app.js 的 fmtDur 保持一致：整小时写“N 小时”，整分钟写“N 分钟”，否则写“N 秒”。
/// </summary>
public static class PolicyText
{
    public static string Duration(TimeSpan span) => DurationSeconds((int)Math.Round(span.TotalSeconds));

    public static string DurationSeconds(int seconds)
    {
        if (seconds >= 3600 && seconds % 3600 == 0)
        {
            return $"{seconds / 3600} 小时";
        }

        if (seconds >= 60 && seconds % 60 == 0)
        {
            return $"{seconds / 60} 分钟";
        }

        return $"{seconds} 秒";
    }

    /// <summary>
    /// 保持运行到期后回到普通模式时的通知正文（SLP-02）。
    /// </summary>
    public static string KeepRunningExpiredToastBody() =>
        $"已回到普通模式：插电闲置 {DurationSeconds(PowerPolicy.NormalAcSleepTimeoutSec)}睡，" +
        $"用电池 {DurationSeconds(PowerPolicy.NormalDcSleepTimeoutSec)}睡，" +
        $"合盖 {Duration(PowerPolicy.NormalLidCloseSleepDelay)}睡";
}
