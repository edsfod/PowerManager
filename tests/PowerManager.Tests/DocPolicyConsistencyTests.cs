using System.Globalization;
using System.Text.RegularExpressions;
using PowerManager.Core.Policy;

namespace PowerManager.Tests;

/// <summary>
/// 保证 docs/requirements.md 的规格与 docs/design.md 的实现参数与 PowerPolicy 一致（ARCHITECTURE.md 不变量 A6）。
/// 正向：由 PowerPolicy 拼出文档中应有的原文；反向：去掉这些原文与登记的例外后，文档中不得再有“数字 + 单位”。
/// 改了 PowerPolicy 而此测试失败时，按失败信息同步修改文档措辞或下方的期望原文。
/// </summary>
public class DocPolicyConsistencyTests
{
    private static readonly Regex NumberWithUnit =
        new(@"(?<![\w$.\-])\d+(?:\.\d+)?\s*(?:%|分钟|小时|秒|°C|MB)", RegexOptions.Compiled);

    private static string D(TimeSpan span) => PolicyText.Duration(span);

    private static string S(int seconds) => PolicyText.DurationSeconds(seconds);

    private static string Sec(TimeSpan span) => $"{(int)span.TotalSeconds} 秒";

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Sleep(int seconds, string suffix) => seconds == 0 ? "从不自动睡眠" : $"{S(seconds)}{suffix}";

    /// <summary>requirements.md：（需求编号, 该小节中应出现的原文）。</summary>
    private static IEnumerable<(string Section, string Text)> RequirementStatements()
    {
        yield return ("SLP-02", $"时长 `{PowerPolicy.KeepRunningMinHours}–{PowerPolicy.KeepRunningMaxHours}` 小时，默认 `{PowerPolicy.KeepRunningDefaultHours}` 小时");

        yield return ("SLP-03", $"插电：闲置 {S(PowerPolicy.NormalAcDisplayTimeoutSec)}关屏，{S(PowerPolicy.NormalAcSleepTimeoutSec)}睡眠");
        yield return ("SLP-03", $"用电池：闲置 {S(PowerPolicy.NormalDcDisplayTimeoutSec)}关屏，{S(PowerPolicy.NormalDcSleepTimeoutSec)}睡眠");

        yield return ("SLP-04", $"插电：闲置 {S(PowerPolicy.KeepRunningAcDisplayTimeoutSec)}关屏，{Sleep(PowerPolicy.KeepRunningAcSleepTimeoutSec, "睡眠")}");
        yield return ("SLP-04", $"插电关屏超时为 {S(PowerPolicy.KeepRunningAcDisplayTimeoutSec)}、睡眠超时为“从不”");

        yield return ("SLP-05", $"用电池闲置 {S(PowerPolicy.KeepRunningDcDisplayTimeoutSec)}关屏，{S(PowerPolicy.KeepRunningDcSleepTimeoutSec)}无人操作睡眠");
        yield return ("SLP-05", $"电池温度达到或超过 {F(PowerPolicy.CommuteBatteryTempThresholdC)} °C");
        yield return ("SLP-05", $"电池温度达到 {F(PowerPolicy.CommuteBatteryTempThresholdC)} °C");
        yield return ("SLP-05", $"开盖且 {D(PowerPolicy.CommuteTempActiveUserSkipIdle)}内有键鼠操作");

        yield return ("SLP-06", $"约 {D(PowerPolicy.NormalLidCloseSleepDelay)}后进入睡眠");

        yield return ("SLP-10", $"主动要求睡眠 {D(PowerPolicy.HibernateFallbackDelay)}后");
        yield return ("SLP-10", $"开盖且 {Sec(PowerPolicy.ForcedSleepActiveUserAbortIdle)}内有键鼠操作");
        yield return ("SLP-10", $"约 {D(PowerPolicy.NormalLidCloseSleepDelay + PowerPolicy.HibernateFallbackDelay)}内进入休眠");

        yield return ("SLP-11", $"{D(PowerPolicy.HibernateFallbackDelay)}后仍未睡下则休眠");
        yield return ("SLP-11", $"{D(PowerPolicy.HibernateFallbackDelay)}后仍醒着时");

        yield return ("PWR-02", $"电量 ≥ {PowerPolicy.DcBalancedMinPercent}% 时");
        yield return ("PWR-02", $"电量 < {PowerPolicy.DcBalancedMinPercent}% 时");
        yield return ("PWR-02", $"电量跨过 {PowerPolicy.DcBalancedMinPercent}%");

        yield return ("CHG-01", $"高于 {PowerPolicy.DefaultChargeStopPercent}% 停止充电，低于 {PowerPolicy.DefaultChargeStartPercent}% 开始充电");

        yield return ("CHG-02", $"目标上限 `{PowerPolicy.BoostTargetMinPercent}–{PowerPolicy.BoostTargetMaxPercent}%`");
        yield return ("CHG-02", $"`{PowerPolicy.BoostHoldMinutesMin}–{PowerPolicy.BoostHoldMinutesMax}` 分钟");
        yield return ("CHG-02", $"`{F(PowerPolicy.BoostMaxHoursMin)}–{F(PowerPolicy.BoostMaxHoursMax)}` 小时");
        yield return ("CHG-02", $"拔电满 {D(PowerPolicy.BoostUnpluggedRevertDelay)}");

        yield return ("CHG-03", $"电量每变化 {PowerPolicy.BatteryHistoryStepPercent}%");

        yield return ("NTF-01", $"超过 {D(PowerPolicy.WakeNotifyMinAwayDuration)}后");
        yield return ("NTF-01", $"真实睡眠累计 ≥ {D(PowerPolicy.WakeSleepMinDuration)}");
    }

    /// <summary>requirements.md 中与 PowerPolicy 无关的数字（原文, 理由）。</summary>
    private static readonly (string Text, string Reason)[] RequirementExceptions =
    [
        ("永久 100% 充电开关", "范围说明（ADR-0003）"),
        ("长按约 10 秒强制关机", "硬件固件行为"),
    ];

    /// <summary>design.md：应出现的原文。</summary>
    private static IEnumerable<string> DesignStatements()
    {
        yield return $"电量到 {PowerPolicy.BatteryHistoryStepPercent}% 的倍数";
        yield return $"通勤温度检查，每 {D(PowerPolicy.CommuteTempCheckInterval)}一次";
        yield return $"主动睡眠检查，每 {D(PowerPolicy.ForcedSleepCheckInterval)}一次";
        yield return $"晚于目标墙上时间 {Sec(PowerPolicy.TimerFrozenDriftThreshold)}以上";
        yield return $"{S(PowerPolicy.ForcedSleepDisplayTimeoutSec)} / {S(PowerPolicy.ForcedSleepTimeoutSec)}";
        yield return $"再加 {D(PowerPolicy.CommuteIdleTakeoverThreshold - TimeSpan.FromSeconds(PowerPolicy.KeepRunningDcSleepTimeoutSec))}余量";
        yield return $"每 {D(PowerPolicy.CommuteTempCheckInterval)}触发 `BatteryCharge` 读温度，{Sec(PowerPolicy.CommuteTempReadDelayAfterTrigger)}后读";
        yield return $"距今不超过 {Sec(PowerPolicy.CommuteTempStateMaxAge)}";
        yield return $"每 {D(PowerPolicy.ForcedSleepCheckInterval)}检查一次";
        yield return $"开盖且 {Sec(PowerPolicy.ForcedSleepActiveUserAbortIdle)}内有键鼠操作";
        yield return $"持续 {D(PowerPolicy.HibernateFallbackDelay)}仍未入睡";
        yield return $"`(目标 - {PowerPolicy.BoostHysteresisPercent})% – 目标%`";
        yield return $"离开超过 {D(PowerPolicy.WakeNotifyMinAwayDuration)}后";
        yield return $"延迟 {Sec(PowerPolicy.WakeLogQueryDelay)}查询";
        yield return $"`[离开时刻 - {Sec(PowerPolicy.WakeLogWindowBeforeOff)}, 回来时刻 + {Sec(PowerPolicy.WakeLogWindowAfterOn)}]`";
        yield return $"累计醒着 ≥ {Sec(PowerPolicy.WakeAwakeAttachmentMinDuration)}";
        yield return $"日志超过 {PowerPolicy.MaxLogFileBytes / (1024 * 1024)} MB";
    }

    /// <summary>design.md 中与 PowerPolicy 无关的数字（原文, 理由）。</summary>
    private static readonly (string Text, string Reason)[] DesignExceptions =
    [
        ("电量每变 1%", "Windows 电量通知粒度"),
        ("`电量 75%`", "reason 字段示例"),
        ("`pmctl boost 95%`", "reason 字段示例"),
        ("失败后每 1 分钟重试，最多 5 次", "计划任务设置，由 install.ps1 定义"),
        ("失败每 1 分钟重启，最多 5 次", "计划任务设置，由 install.ps1 定义"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PowerManager.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录（PowerManager.sln）。");
    }

    private static string Doc(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "docs", name));

    private static string Section(string doc, string id)
    {
        int start = doc.IndexOf($"### {id} ", StringComparison.Ordinal);
        Assert.True(start >= 0, $"requirements.md 缺少小节 {id}。");
        int next = doc.IndexOf("\n#", start + 4, StringComparison.Ordinal);
        return next < 0 ? doc[start..] : doc[start..next];
    }

    [Fact]
    public void Requirements_SpecMatchesPowerPolicy()
    {
        string doc = Doc("requirements.md");
        var missing = RequirementStatements()
            .Where(s => !Section(doc, s.Section).Contains(s.Text, StringComparison.Ordinal))
            .Select(s => $"{s.Section}: “{s.Text}”")
            .ToList();

        Assert.True(missing.Count == 0,
            "requirements.md 与 PowerPolicy 不一致，以下原文（由 PowerPolicy 拼出）未找到：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Design_ParametersMatchPowerPolicy()
    {
        string doc = Doc("design.md");
        var missing = DesignStatements().Where(t => !doc.Contains(t, StringComparison.Ordinal)).Select(t => $"“{t}”").ToList();

        Assert.True(missing.Count == 0,
            "design.md 与 PowerPolicy 不一致，以下原文（由 PowerPolicy 拼出）未找到：\n" + string.Join("\n", missing));
    }

    [Theory]
    [InlineData("requirements.md")]
    [InlineData("design.md")]
    public void Docs_HaveNoUnregisteredNumbers(string file)
    {
        bool isRequirements = file == "requirements.md";
        IEnumerable<string> known = isRequirements
            ? RequirementStatements().Select(s => s.Text).Concat(RequirementExceptions.Select(e => e.Text))
            : DesignStatements().Concat(DesignExceptions.Select(e => e.Text));

        string doc = Doc(file);
        foreach (string text in known.OrderByDescending(t => t.Length))
        {
            doc = doc.Replace(text, string.Empty, StringComparison.Ordinal);
        }

        var hits = new List<string>();
        string[] lines = doc.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in NumberWithUnit.Matches(lines[i]))
            {
                hits.Add($"“{m.Value}”：{lines[i].Trim()}");
            }
        }

        Assert.True(hits.Count == 0,
            $"{file} 出现未登记的“数字 + 单位”。来自 PowerPolicy 的写进本测试的期望原文；与策略无关的登记为例外并写明理由：\n" +
            string.Join("\n", hits));
    }
}
