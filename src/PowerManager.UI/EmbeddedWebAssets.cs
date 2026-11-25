using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PowerManager.UI;

/// <summary>
/// 负责读取内嵌前端静态资源（拼装为单页 HTML 供 WebView2 加载）、内嵌图标以及 Windows 深浅色标题栏同步。
/// </summary>
internal static class EmbeddedWebAssets
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static string BuildInlineHtml()
    {
        string html = ReadEmbeddedText("PowerManager.UI.web.index.html");
        string css = ReadEmbeddedText("PowerManager.UI.web.styles.css");
        string mockJs = ReadEmbeddedText("PowerManager.UI.web.mock.js");
        string appJs = ReadEmbeddedText("PowerManager.UI.web.app.js");

        html = ReplaceRequired(html, "<link rel=\"stylesheet\" href=\"styles.css\">", $"<style>\n{css}\n</style>");
        html = ReplaceRequired(html, "<script src=\"mock.js\"></script>", $"<script>\n{mockJs}\n</script>");
        html = ReplaceRequired(html, "<script src=\"app.js\"></script>", $"<script>\n{appJs}\n</script>");
        return html;
    }

    public static Icon? LoadEmbeddedIcon()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("PowerManager.UI.Assets.icon.ico");
        return stream is null ? null : new Icon(stream);
    }

    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Color GetThemeBackground(bool dark) =>
        dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);

    public static void ApplyTitleBarTheme(IntPtr hwnd, bool dark)
    {
        int useDark = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
    }

    private static string ReadEmbeddedText(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"缺少内嵌前端资源 {logicalName}。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ReplaceRequired(string source, string marker, string replacement)
    {
        if (!source.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"内嵌前端模板缺少占位标记：{marker}");
        }

        return source.Replace(marker, replacement, StringComparison.Ordinal);
    }
}
