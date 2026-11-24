namespace PowerManager.Platform.Storage;

/// <summary>
/// 运行时数据目录路径解析（ADR-0006）。
/// 生产默认 %LOCALAPPDATA%\PowerManager，开发验证期可通过 --data-dir 覆盖（如 ./var）。
/// </summary>
public sealed class AppPaths
{
    public string DataDir { get; }

    public string AgentDir => Path.Combine(DataDir, "agent");
    public string OriginalJsonPath => Path.Combine(AgentDir, "original.json");
    public string ModeJsonPath => Path.Combine(AgentDir, "mode.json");
    public string AgentLogPath => Path.Combine(AgentDir, "agent.log");
    public string AgentErrorLogPath => Path.Combine(AgentDir, "error.log");

    public string BatteryChargeDir => Path.Combine(DataDir, "battery-charge");
    public string StateJsonPath => Path.Combine(BatteryChargeDir, "state.json");
    public string EcLogPath => Path.Combine(BatteryChargeDir, "ec.log");
    public string HistoryLogPath => Path.Combine(BatteryChargeDir, "history.log");
    public string BatteryErrorLogPath => Path.Combine(BatteryChargeDir, "error.log");

    public string UiDir => Path.Combine(DataDir, "ui");
    public string PrefsJsonPath => Path.Combine(UiDir, "prefs.json");

    public string RequestsDir => Path.Combine(DataDir, "requests");

    public AppPaths(string? explicitDataDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataDir))
        {
            DataDir = Path.GetFullPath(explicitDataDir);
            return;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法解析 %LOCALAPPDATA%，请通过 --data-dir 显式指定数据目录。");
        }

        DataDir = Path.Combine(localAppData, "PowerManager");
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(AgentDir);
        Directory.CreateDirectory(BatteryChargeDir);
        Directory.CreateDirectory(UiDir);
        Directory.CreateDirectory(RequestsDir);
    }

    /// <summary>
    /// 从命令行参数中提取 --data-dir 值（支持 --data-dir &lt;path&gt; 与 --data-dir=&lt;path&gt;）。
    /// </summary>
    public static string? ParseDataDirArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            const string prefix = "--data-dir=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i][prefix.Length..];
            }
        }

        return null;
    }
}
