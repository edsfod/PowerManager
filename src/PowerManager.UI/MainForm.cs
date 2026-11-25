using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using PowerManager.Platform.Storage;

namespace PowerManager.UI;

internal sealed class MainForm : Form
{
    private readonly AppPaths _paths;
    private readonly bool _debug;
    private readonly bool _smokeTest;
    private readonly WebView2 _webView;
    private readonly WindowWidthAnimator _widthAnimator;
    private readonly WebRpcBridge _rpcBridge;

    public bool SmokeTestPassed { get; private set; }

    public MainForm(AppPaths paths, UiBackendService backend, bool debug, bool smokeTest)
    {
        _paths = paths;
        _debug = debug;
        _smokeTest = smokeTest;

        bool dark = EmbeddedWebAssets.IsSystemDarkMode();
        Color bg = EmbeddedWebAssets.GetThemeBackground(dark);

        Text = "PowerManager";
        BackColor = bg;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = EmbeddedWebAssets.LoadEmbeddedIcon();

        _widthAnimator = new WindowWidthAnimator(this);
        _widthAnimator.ApplyInitialBounds(expanded: backend.GetRulesExpandedPref());

        _rpcBridge = new WebRpcBridge(
            backend,
            onSetExpanded: _widthAnimator.AnimateTo,
            onCloseWindow: () => BeginInvoke(Close),
            onSmokeReady: () =>
            {
                SmokeTestPassed = true;
                if (_smokeTest)
                {
                    BeginInvoke(Close);
                }
            });

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = bg,
            AllowExternalDrop = false
        };
        Controls.Add(_webView);

        Shown += OnFormShownAsync;
        Activated += OnFormActivated;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EmbeddedWebAssets.ApplyTitleBarTheme(Handle, EmbeddedWebAssets.IsSystemDarkMode());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _widthAnimator.Dispose();
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

            bool dark = EmbeddedWebAssets.IsSystemDarkMode();
            Color bg = EmbeddedWebAssets.GetThemeBackground(dark);
            BackColor = bg;
            _webView.DefaultBackgroundColor = bg;
            EmbeddedWebAssets.ApplyTitleBarTheme(Handle, dark);
        });
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
            _webView.CoreWebView2.NavigateToString(EmbeddedWebAssets.BuildInlineHtml());

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
        string? responseJson = await _rpcBridge.HandleMessageAsync(e.WebMessageAsJson);
        if (responseJson is not null && _webView.CoreWebView2 is not null && !IsDisposed)
        {
            _webView.CoreWebView2.PostWebMessageAsJson(responseJson);
        }
    }
}
