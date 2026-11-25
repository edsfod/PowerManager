using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerManager.Core.Policy;

namespace PowerManager.Tests;

/// <summary>
/// 保证界面文案与通知中的策略数字只来自 PowerPolicy：
/// 前端不手写“数字 + 单位”，离线预览 mock.js 的 policy 副本与 PolicySnapshot 一致。
/// </summary>
public class UiPolicyConsistencyTests
{
    private static readonly Regex HardcodedPolicyNumber =
        new(@"(?<![\w$.\-])\d+(?:\.\d+)?\s*(?:%|分钟|小时|秒|°C)", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PowerManager.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录（PowerManager.sln）。");
    }

    private static string WebFile(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "PowerManager.UI", "web", name));

    [Theory]
    [InlineData("index.html")]
    [InlineData("app.js")]
    public void Frontend_HasNoHardcodedPolicyNumbers(string file)
    {
        string[] lines = WebFile(file).Split('\n');
        var hits = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in HardcodedPolicyNumber.Matches(lines[i]))
            {
                hits.Add($"{file}:{i + 1}: \"{m.Value}\"");
            }
        }

        Assert.True(hits.Count == 0,
            "前端出现手写的策略数字，请改为读取 S.policy（index.html 用 data-policy 占位）：\n" + string.Join("\n", hits));
    }

    [Fact]
    public void MockPolicy_MatchesPolicySnapshot()
    {
        string mock = WebFile("mock.js");
        int begin = mock.IndexOf("/* policy:begin */", StringComparison.Ordinal);
        int end = mock.IndexOf("/* policy:end */", StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "mock.js 缺少 /* policy:begin */ … /* policy:end */ 标记。");

        var mockValues = Regex.Matches(mock[begin..end], @"(\w+):\s*(-?\d+(?:\.\d+)?)")
            .ToDictionary(m => m.Groups[1].Value, m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(PolicySnapshot.Current));
        var expected = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());

        Assert.Equal(expected.Keys.OrderBy(k => k), mockValues.Keys.OrderBy(k => k));
        foreach (var (key, value) in expected)
        {
            Assert.True(mockValues[key] == value, $"mock.js policy.{key} = {mockValues[key]}，应为 {value}（PowerPolicy）。");
        }
    }

    [Theory]
    [InlineData(3600, "1 小时")]
    [InlineData(7200, "2 小时")]
    [InlineData(900, "15 分钟")]
    [InlineData(5400, "90 分钟")]
    [InlineData(45, "45 秒")]
    public void PolicyText_DurationSeconds_MatchesFrontendFmtDur(int seconds, string expected)
    {
        Assert.Equal(expected, PolicyText.DurationSeconds(seconds));
    }

    [Fact]
    public void PolicyText_KeepRunningExpiredToast_UsesPolicyValues()
    {
        string body = PolicyText.KeepRunningExpiredToastBody();
        Assert.Contains($"插电闲置 {PolicyText.DurationSeconds(PowerPolicy.NormalAcSleepTimeoutSec)}睡", body);
        Assert.Contains($"用电池 {PolicyText.DurationSeconds(PowerPolicy.NormalDcSleepTimeoutSec)}睡", body);
        Assert.Contains($"合盖 {PolicyText.Duration(PowerPolicy.NormalLidCloseSleepDelay)}睡", body);
    }
}
