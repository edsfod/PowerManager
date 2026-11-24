using System.Text;
using PowerManager.Core.Policy;

namespace PowerManager.Platform.Storage;

/// <summary>
/// 线程安全 UTC ISO 8601 追加日志，超过 1 MB 自动轮换为 .1（docs/design.md §五）。
/// </summary>
public sealed class RotatingUtcLogger(string logPath, long maxBytes = PowerPolicy.MaxLogFileBytes)
{
    private readonly object _gate = new();
    private readonly string _logPath = logPath;
    private readonly long _maxBytes = maxBytes;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message, DateTimeOffset? utcNow = null)
    {
        DateTimeOffset ts = (utcNow ?? DateTimeOffset.UtcNow).ToUniversalTime();
        string line = $"{ts:yyyy-MM-ddTHH:mm:ssZ} [{level}] {message}{Environment.NewLine}";
        AppendRawLine(line);
    }

    public void AppendRawLine(string lineWithNewline)
    {
        lock (_gate)
        {
            string? dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            RotateIfNeeded();
            File.AppendAllText(_logPath, lineWithNewline, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath))
        {
            return;
        }

        var info = new FileInfo(_logPath);
        if (info.Length < _maxBytes)
        {
            return;
        }

        string rotated = _logPath + ".1";
        File.Move(_logPath, rotated, overwrite: true);
    }
}
