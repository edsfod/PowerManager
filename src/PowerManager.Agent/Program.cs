using PowerManager.Core.Contracts;
using PowerManager.Platform.Storage;

namespace PowerManager.Agent;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = new AppPaths(AppPaths.ParseDataDirArg(args));
        bool dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        string pipeName = GetOption(args, "--pipe-name") ?? IpcConstants.AgentPipeName;

        // 单实例互斥锁（按 pipeName 区分，确保开发测试实例与生产实例互不冲突）
        using var mutex = new Mutex(initiallyOwned: true, name: $@"Local\PowerManager.Agent.{pipeName}", out bool createdNew);
        if (!createdNew)
        {
            return 0;
        }

        var errorLog = new RotatingUtcLogger(paths.AgentErrorLogPath);
        try
        {
            using var controller = new AgentController(paths, pipeName, dryRun);
            controller.Run();
            return 0;
        }
        catch (Exception ex)
        {
            errorLog.Error(ex.ToString());
            return 1;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            string prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i][prefix.Length..];
            }
        }

        return null;
    }
}
