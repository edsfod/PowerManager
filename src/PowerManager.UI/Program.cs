using PowerManager.Core.Contracts;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.UI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ToastNotifierHelper.SetProcessAppUserModelId(IpcConstants.AppUserModelId);

        bool debug = args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase));
        bool dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        bool smokeTest = args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        string? dataDir = AppPaths.ParseDataDirArg(args);
        string pipeName = ParseOption(args, "--pipe-name") ?? IpcConstants.AgentPipeName;

        var paths = new AppPaths(dataDir);
        var backend = new UiBackendService(paths, pipeName, dryRun);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new MainForm(paths, backend, debug, smokeTest);
        Application.Run(form);

        if (smokeTest)
        {
            return form.SmokeTestPassed ? 0 : 1;
        }

        return 0;
    }

    private static string? ParseOption(string[] args, string option)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            string prefix = option + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i][prefix.Length..];
            }
        }

        return null;
    }
}
