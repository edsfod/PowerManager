using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using PowerManager.Platform.Storage;

namespace PowerManager.UI;

internal sealed class MainForm : Form
{
    private const int CompactLogicalWidth = 720;
    private const int ExpandedLogicalWidth = 960;
    private const int InitialLogicalHeight = 880;
    private const int MinLogicalWidth = 520;
    private const int MinLogicalHeight = 600;

    private readonly AppPaths _paths;
    private readonly UiBackendService _backend;
    private readonly bool _debug;
    private readonly bool _smokeTest;
    private readonly WebView2 _webView;
    private readonly System.Windows.Forms.Timer _animTimer;

    private int _animStep;
    private const int AnimTotalSteps = 12;
    private int _animStartW;
    private int _animTargetW;
    private double _animCenterX;
    private bool _animSnapLeft;
    private bool _animSnapRight;
    private Rectangle _animWorkArea;

    public bool SmokeTestPassed { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public MainForm(AppPaths paths, UiBackendService backend, bool debug, bool smokeTest)
    {
        _paths = paths;
        _backend = backend;
        _debug = debug;
        _smokeTest = smokeTest;

        bool dark = IsSystemDarkMode();
        Color bg = GetThemeBackground(dark);

        Text = "PowerManager";
        BackColor = bg;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = LoadEmbeddedIcon();

        ApplyInitialBounds(expanded: _backend.GetRulesExpandedPref());

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = bg,
            AllowExternalDrop = false
        };
        Controls.Add(_webView);

        _animTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _animTimer.Tick += OnAnimateTick;

        Shown += OnFormShownAsync;
        Activated += OnFormActivated;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme(IsSystemDarkMode());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _animTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            bool dark = IsSystemDarkMode();
            Color bg = GetThemeBackground(dark);
            BackColor = bg;
            _webView.DefaultBackgroundColor = bg;
            ApplyTitleBarTheme(dark);
        });
    }

    private void ApplyTitleBarTheme(bool dark)
    {
        int useDark = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
    }

    private static Color GetThemeBackground(bool dark) =>
        dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);

    private void ApplyInitialBounds(bool expanded)
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        float scale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
        int logicalW = expanded ? ExpandedLogicalWidth : CompactLogicalWidth;
        int w = Math.Min(screen.Width, (int)Math.Round(logicalW * scale));
        int h = Math.Min((int)(screen.Height * 0.94), (int)Math.Round(InitialLogicalHeight * scale));
        Size = new Size(w, h);
        MinimumSize = new Size(
            (int)Math.Round(MinLogicalWidth * scale),
            (int)Math.Round(MinLogicalHeight * scale));
    }

    private async void OnFormShownAsync(object? sender, EventArgs e)
    {
        try
        {
            string userDataFolder = Path.Combine(_paths.UiDir, "webview2");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);

            await _webView.EnsureCoreWebView2Async(env);

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = _debug;
            settings.AreDevToolsEnabled = _debug;
            settings.AreBrowserAcceleratorKeysEnabled = _debug;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigateToString(LoadEmbeddedHtml());

            if (_smokeTest)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(8000);
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(Close);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            if (!_smokeTest)
            {
                MessageBox.Show(
                    $"无法初始化 WebView2 运行时：{ex.Message}\n请确认系统已安装 Microsoft Edge WebView2 Runtime。",
                    "PowerManager",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            Close();
        }
    }

    private void OnFormActivated(object? sender, EventArgs e)
    {
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.PostWebMessageAsJson("{\"event\":\"focus\"}");
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        RpcRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<RpcRequest>(e.WebMessageAsJson, JsonOpts);
        }
        catch
        {
            return;
        }

        if (req is null || string.IsNullOrWhiteSpace(req.Method))
        {
            return;
        }

        object? result = null;
        string? error = null;

        try
        {
            switch (req.Method)
            {
                case "get_state":
                    result = await _backend.GetStateAsync();
                    break;

                case "set_mode":
                {
                    string mode = req.Params?.GetPropertyOrDefault("mode") ?? "normal";
                    int? hours = req.Params?.GetIntOrDefault("hours");
                    result = await _backend.SetModeAsync(mode, hours);
                    break;
                }

                case "boost":
                {
                    int target = req.Params?.GetIntOrDefault("target") ?? 95;
                    int hold = req.Params?.GetIntOrDefault("hold") ?? 60;
                    double maxh = req.Params?.GetDoubleOrDefault("maxh") ?? 4.0;
                    result = await _backend.RequestBoostAsync(target, hold, maxh);
                    break;
                }

                case "refresh_ec":
                    result = await _backend.RefreshEcAsync();
                    break;

                case "open_data_dir":
                    result = _backend.OpenDataDir();
                    break;

                case "start_agent":
                    result = await _backend.StartAgentAsync();
                    break;

                case "set_expanded":
                {
                    bool expanded = req.Params?.GetBoolOrDefault("expanded") ?? false;
                    _backend.SaveRulesExpandedPref(expanded);
                    AnimateWidth(expanded);
                    result = new UiActionResult(true);
                    break;
                }

                case "close_window":
                    result = new UiActionResult(true);
                    BeginInvoke(Close);
                    break;

                case "smoke_ready":
                {
                    SmokeTestPassed = true;
                    result = new UiActionResult(true);
                    if (_smokeTest)
                    {
                        BeginInvoke(Close);
                    }
                    break;
                }

                default:
                    error = $"未知方法：{req.Method}";
                    break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (req.Id.HasValue && _webView.CoreWebView2 is not null && !IsDisposed)
        {
            var resp = new RpcResponse(req.Id.Value, result, error);
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, JsonOpts));
        }
    }

    private void AnimateWidth(bool expanded)
    {
        if (expanded)
        {
            if (WindowState == FormWindowState.Maximized)
            {
                return;
            }
        }
        else if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }

        _animWorkArea = Screen.FromControl(this).WorkingArea;
        float scale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
        int logical = expanded ? ExpandedLogicalWidth : CompactLogicalWidth;
        _animTargetW = Math.Min(_animWorkArea.Width, (int)Math.Round(logical * scale));
        _animStartW = Width;
        if (_animStartW == _animTargetW)
        {
            return;
        }

        _animSnapLeft = Math.Abs(Left - _animWorkArea.Left) <= 12;
        _animSnapRight = !_animSnapLeft && Math.Abs(Right - _animWorkArea.Right) <= 12;
        _animCenterX = Left + Width / 2.0;
        _animStep = 0;
        _animTimer.Start();
    }

    private void OnAnimateTick(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Maximized)
        {
            _animTimer.Stop();
            return;
        }

        _animStep++;
        double p = Math.Min(1.0, (double)_animStep / AnimTotalSteps);
        double eased = 1.0 - Math.Pow(1.0 - p, 3);
        int curW = (int)Math.Round(_animStartW + (_animTargetW - _animStartW) * eased);

        int x;
        if (_animSnapLeft)
        {
            x = _animWorkArea.Left;
        }
        else if (_animSnapRight)
        {
            x = _animWorkArea.Right - curW;
        }
        else
        {
            x = Math.Clamp((int)Math.Round(_animCenterX - curW / 2.0), _animWorkArea.Left, _animWorkArea.Right - curW);
        }

        SetBounds(x, Top, curW, Height);

        if (_animStep >= AnimTotalSteps)
        {
            _animTimer.Stop();
        }
    }

    private static string LoadEmbeddedHtml()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("PowerManager.UI.web.index.html")
            ?? throw new InvalidOperationException("缺少内嵌前端资源 PowerManager.UI.web.index.html。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Icon? LoadEmbeddedIcon()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("PowerManager.UI.Assets.icon.ico");
        return stream is null ? null : new Icon(stream);
    }

    private static bool IsSystemDarkMode()
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

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private sealed record RpcRequest(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] JsonElement? Params);

    private sealed record RpcResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("result")] object? Result,
        [property: JsonPropertyName("error")] string? Error);
}

internal static class JsonElementExtensions
{
    public static string? GetPropertyOrDefault(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    public static int? GetIntOrDefault(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int v))
        {
            return v;
        }

        return null;
    }

    public static double? GetDoubleOrDefault(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double v))
        {
            return v;
        }

        return null;
    }

    public static bool? GetBoolOrDefault(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
