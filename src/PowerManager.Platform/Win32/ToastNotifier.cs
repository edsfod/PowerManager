using System.Runtime.InteropServices;
using System.Security;
using PowerManager.Core.Contracts;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PowerManager.Platform.Win32;

/// <summary>
/// .NET 原生 WinRT Toast 通知与进程 AppUserModelID 设置（NTF-01 / SLP-02）。
/// </summary>
public static class ToastNotifierHelper
{
    public static void SetProcessAppUserModelId(string appId = IpcConstants.AppUserModelId)
    {
        _ = SetCurrentProcessExplicitAppUserModelID(appId);
    }

    public static void ShowToast(string title, string body, string appId = IpcConstants.AppUserModelId)
    {
        var lines = body.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var textNodes = new List<string> { $"<text>{EscapeXml(title)}</text>" };
        foreach (string line in lines.Take(3))
        {
            textNodes.Add($"<text>{EscapeXml(line)}</text>");
        }

        string toastXml =
            $"<toast><visual><binding template=\"ToastGeneric\">{string.Join(string.Empty, textNodes)}</binding></visual></toast>";

        var doc = new XmlDocument();
        doc.LoadXml(toastXml);

        var toast = new ToastNotification(doc);
        var notifier = ToastNotificationManager.CreateToastNotifier(appId);
        notifier.Show(toast);
    }

    private static string EscapeXml(string value) => SecurityElement.Escape(value) ?? string.Empty;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
