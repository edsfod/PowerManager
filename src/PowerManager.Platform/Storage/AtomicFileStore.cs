using System.Text;
using System.Text.Json;
using PowerManager.Core.Contracts;

namespace PowerManager.Platform.Storage;

/// <summary>
/// 原子 JSON 文件读写与 &lt;DataDir&gt;/requests/*.json 白名单请求队列。
/// </summary>
public static class AtomicFileStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static void WriteJsonAtomic<T>(string path, T value)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(path) && IsReparsePoint(path))
        {
            throw new IOException($"拒绝写入符号链接/重解析点目标：{path}");
        }

        // 临时文件必须建在目标同级目录，确保同卷原子替换并继承目标目录 ACL
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(value, JsonOptions);
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* 清理失败忽略 */ }
            }
        }
    }

    public static T? ReadJsonOrDefault<T>(string path)
    {
        if (!File.Exists(path) || IsReparsePoint(path))
        {
            return default;
        }

        string json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public static string WriteEcRequestAtomic(AppPaths paths, EcTaskRequest request)
    {
        if (!request.TryValidate(out var error))
        {
            throw new ArgumentException(error, nameof(request));
        }

        Directory.CreateDirectory(paths.RequestsDir);
        string fileName = $"{request.CreatedAtUtc.UtcTicks:D20}-{Guid.NewGuid():N}.json";
        string fullPath = Path.Combine(paths.RequestsDir, fileName);
        WriteJsonAtomic(fullPath, request);
        return fullPath;
    }

    /// <summary>
    /// 按时间顺序消费 &lt;DataDir&gt;/requests/*.json：严格校验白名单边界，拒绝符号链接/重解析点，消费后删除。
    /// </summary>
    public static IReadOnlyList<EcTaskRequest> ConsumeEcRequests(
        AppPaths paths,
        Action<string>? onRejected = null)
    {
        if (!Directory.Exists(paths.RequestsDir) || IsReparsePoint(paths.RequestsDir))
        {
            return [];
        }

        var files = Directory.GetFiles(paths.RequestsDir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        var accepted = new List<EcTaskRequest>();
        foreach (string file in files)
        {
            try
            {
                if (IsReparsePoint(file))
                {
                    onRejected?.Invoke($"跳过重解析点请求文件：{Path.GetFileName(file)}");
                    continue;
                }

                string json = File.ReadAllText(file, Encoding.UTF8);
                var req = JsonSerializer.Deserialize<EcTaskRequest>(json, JsonOptions);
                if (req is null)
                {
                    onRejected?.Invoke($"请求文件为空：{Path.GetFileName(file)}");
                }
                else if (!req.TryValidate(out var error))
                {
                    onRejected?.Invoke($"请求校验未通过（{Path.GetFileName(file)}）：{error}");
                }
                else
                {
                    accepted.Add(req);
                }
            }
            catch (Exception ex)
            {
                onRejected?.Invoke($"解析请求文件失败（{Path.GetFileName(file)}）：{ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(file) && !IsReparsePoint(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 忽略删除异常，避免阻塞后续任务
                }
            }
        }

        return accepted;
    }

    public static bool IsReparsePoint(string path)
    {
        var attrs = File.GetAttributes(path);
        return (attrs & FileAttributes.ReparsePoint) != 0;
    }
}
