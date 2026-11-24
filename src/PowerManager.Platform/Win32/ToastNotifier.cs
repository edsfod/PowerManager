using System.Runtime.InteropServices;
using System.Security;
using PowerManager.Core.Contracts;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PowerManager.Platform.Win32;

/// <summary>
/// .NET 原生 WinRT Toast 通知与进程 AppUserModelID 设置（NTF-01 / SLP-02）。
/// 遵循 docs/reference/windows-standby-and-toast.md §三 契约：静音、Tag/Group 覆盖、圆形 Logo。
/// </summary>
public static class ToastNotifierHelper
{
    public const string DefaultGroup = "PowerManager";

    public static void SetProcessAppUserModelId(string appId = IpcConstants.AppUserModelId)
    {
        _ = SetCurrentProcessExplicitAppUserModelID(appId);
    }

    public static string BuildToastXml(string title, string body, string? iconPath = null)
    {
        string logoNode = string.Empty;
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            string iconUri = new Uri(Path.GetFullPath(iconPath)).AbsoluteUri;
            logoNode = $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"{EscapeXml(iconUri)}\"/>";
        }

        var lines = body.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var textNodes = new List<string> { $"<text hint-maxLines=\"1\">{EscapeXml(title)}</text>" };
        foreach (string line in lines.Take(2))
        {
            textNodes.Add($"<text>{EscapeXml(line)}</text>");
        }

        return $"<toast><visual><binding template=\"ToastGeneric\">{logoNode}{string.Join(string.Empty, textNodes)}</binding></visual><audio silent=\"true\"/></toast>";
    }

    public static void ShowToast(
        string title,
        string body,
        string tag = "status",
        string appId = IpcConstants.AppUserModelId,
        string? iconPath = null)
    {
        string resolvedIcon = iconPath ?? Path.Combine(AppContext.BaseDirectory, "icon.png");
        string toastXml = BuildToastXml(title, body, resolvedIcon);

        var doc = new XmlDocument();
        doc.LoadXml(toastXml);

        var toast = new ToastNotification(doc)
        {
            Tag = tag,
            Group = DefaultGroup
        };

        var notifier = ToastNotificationManager.CreateToastNotifier(appId);
        notifier.Show(toast);
    }

    private static string EscapeXml(string value) => SecurityElement.Escape(value) ?? string.Empty;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
