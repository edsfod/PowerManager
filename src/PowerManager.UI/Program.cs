using System.Runtime.InteropServices;
using PowerManager.Core.Contracts;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.UI;

internal static class Program
{
    private const int SwRestore = 9;

    [STAThread]
    private static int Main(string[] args)
    {
        ToastNotifierHelper.SetProcessAppUserModelId(IpcConstants.AppUserModelId);

        bool debug = args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase));
        bool dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        bool smokeTest = args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        string? dataDir = AppPaths.ParseDataDirArg(args);
        string pipeName = ParseOption(args, "--pipe-name") ?? IpcConstants.AgentPipeName;

        // 单实例防多开：按 pipeName 作用域隔离，避免测试实例与生产实例冲突
        string mutexName = $"Local\\PowerManager.UI.{pipeName}";
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, name: mutexName, out bool createdNew);
        if (!createdNew && !smokeTest)
        {
            ActivateExistingWindow("PowerManager");
            return 0;
        }

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

    private static void ActivateExistingWindow(string windowTitle)
    {
        IntPtr hwnd = FindWindowW(null, windowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(hwnd))
        {
            _ = ShowWindow(hwnd, SwRestore);
        }

        _ = SetForegroundWindow(hwnd);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
