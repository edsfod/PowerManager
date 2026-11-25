namespace PowerManager.UI;

/// <summary>
/// 管理窗口的 DPI 自适应边界、内容驱动高度贴合（Intrinsic Sizing）与展开/收起规则面板时的平滑宽度动画。
/// </summary>
internal sealed class WindowWidthAnimator : IDisposable
{
    private const int CompactLogicalWidth = 720;
    private const int ExpandedLogicalWidth = 960;
    private const int BootstrapLogicalHeight = 780;
    private const int MinLogicalWidth = 520;
    private const int MinLogicalHeight = 520;
    private const int AnimTotalSteps = 12;

    private readonly Form _form;
    private readonly System.Windows.Forms.Timer _timer;

    private int _step;
    private int _startW;
    private int _targetW;
    private double _centerX;
    private bool _snapLeft;
    private bool _snapRight;
    private Rectangle _workArea;
    private bool _hasFittedContentHeight;
    private bool _userResizedHeight;
    private bool _applyingHeightBounds;
    private int _lastAppliedWindowHeight;
    private double _anchorCenterY;

    public WindowWidthAnimator(Form form)
    {
        _form = form;
        _timer = new System.Windows.Forms.Timer { Interval = 15 };
        _timer.Tick += OnTick;
        _form.ResizeEnd += OnFormResizeEnd;
        _form.LocationChanged += OnFormLocationChanged;
    }

    public void ApplyInitialBounds(bool expanded)
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        float scale = _form.DeviceDpi > 0 ? _form.DeviceDpi / 96f : 1f;
        int logicalW = expanded ? ExpandedLogicalWidth : CompactLogicalWidth;
        int w = Math.Min(screen.Width, (int)Math.Round(logicalW * scale));
        int h = Math.Min((int)(screen.Height * 0.94), (int)Math.Round(BootstrapLogicalHeight * scale));
        _form.Size = new Size(w, h);
        _lastAppliedWindowHeight = h;
        _form.MinimumSize = new Size(
            (int)Math.Round(MinLogicalWidth * scale),
            (int)Math.Round(MinLogicalHeight * scale));
    }

    public void ApplyContentHeight(double cssLogicalHeight)
    {
        if (_form.IsDisposed || _form.WindowState == FormWindowState.Maximized || _userResizedHeight)
        {
            return;
        }

        var workArea = Screen.FromControl(_form).WorkingArea;
        float scale = _form.DeviceDpi > 0 ? _form.DeviceDpi / 96f : 1f;
        int chromeH = Math.Max(0, _form.Height - _form.ClientSize.Height);
        int maxWindowH = (int)(workArea.Height * 0.94);
        int minWindowH = _form.MinimumSize.Height;
        int targetWindowH = Math.Clamp((int)Math.Ceiling(cssLogicalHeight * scale) + chromeH, minWindowH, maxWindowH);

        if (!_hasFittedContentHeight)
        {
            _anchorCenterY = workArea.Top + workArea.Height / 2.0;
            _hasFittedContentHeight = true;
        }

        int newTop = Math.Clamp(
            (int)Math.Round(_anchorCenterY - targetWindowH / 2.0),
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - targetWindowH));

        if (Math.Abs(_form.Height - targetWindowH) <= 2 && Math.Abs(_form.Top - newTop) <= 2)
        {
            _lastAppliedWindowHeight = _form.Height;
            return;
        }

        _lastAppliedWindowHeight = targetWindowH;
        _applyingHeightBounds = true;
        try
        {
            _form.SetBounds(_form.Left, newTop, _form.Width, targetWindowH);
        }
        finally
        {
            _applyingHeightBounds = false;
        }
    }

    private void OnFormLocationChanged(object? sender, EventArgs e)
    {
        if (!_applyingHeightBounds && _hasFittedContentHeight && _form.WindowState == FormWindowState.Normal)
        {
            _anchorCenterY = _form.Top + _form.Height / 2.0;
        }
    }

    private void OnFormResizeEnd(object? sender, EventArgs e)
    {
        if (_form.WindowState == FormWindowState.Normal &&
            _hasFittedContentHeight &&
            Math.Abs(_form.Height - _lastAppliedWindowHeight) > 4)
        {
            _userResizedHeight = true;
        }
    }

    public void AnimateTo(bool expanded)
    {
        if (expanded)
        {
            if (_form.WindowState == FormWindowState.Maximized)
            {
                return;
            }
        }
        else if (_form.WindowState == FormWindowState.Maximized)
        {
            _form.WindowState = FormWindowState.Normal;
        }

        _workArea = Screen.FromControl(_form).WorkingArea;
        float scale = _form.DeviceDpi > 0 ? _form.DeviceDpi / 96f : 1f;
        int logical = expanded ? ExpandedLogicalWidth : CompactLogicalWidth;
        _targetW = Math.Min(_workArea.Width, (int)Math.Round(logical * scale));
        _startW = _form.Width;
        if (_startW == _targetW)
        {
            return;
        }

        _snapLeft = Math.Abs(_form.Left - _workArea.Left) <= 12;
        _snapRight = !_snapLeft && Math.Abs(_form.Right - _workArea.Right) <= 12;
        _centerX = _form.Left + _form.Width / 2.0;
        _step = 0;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_form.WindowState == FormWindowState.Maximized)
        {
            _timer.Stop();
            return;
        }

        _step++;
        double p = Math.Min(1.0, (double)_step / AnimTotalSteps);
        double eased = 1.0 - Math.Pow(1.0 - p, 3);
        int curW = (int)Math.Round(_startW + (_targetW - _startW) * eased);

        int x;
        if (_snapLeft)
        {
            x = _workArea.Left;
        }
        else if (_snapRight)
        {
            x = _workArea.Right - curW;
        }
        else
        {
            x = Math.Clamp((int)Math.Round(_centerX - curW / 2.0), _workArea.Left, _workArea.Right - curW);
        }

        _form.SetBounds(x, _form.Top, curW, _form.Height);

        if (_step >= AnimTotalSteps)
        {
            _timer.Stop();
        }
    }

    public void Dispose()
    {
        _form.ResizeEnd -= OnFormResizeEnd;
        _form.LocationChanged -= OnFormLocationChanged;
        _timer.Dispose();
    }
}
