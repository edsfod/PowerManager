namespace PowerManager.UI;

/// <summary>
/// 管理窗口的 DPI 自适应初始尺寸与展开/收起规则面板时的平滑宽度动画（含左右贴边吸附）。
/// </summary>
internal sealed class WindowWidthAnimator : IDisposable
{
    private const int CompactLogicalWidth = 720;
    private const int ExpandedLogicalWidth = 960;
    private const int InitialLogicalHeight = 880;
    private const int MinLogicalWidth = 520;
    private const int MinLogicalHeight = 600;
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

    public WindowWidthAnimator(Form form)
    {
        _form = form;
        _timer = new System.Windows.Forms.Timer { Interval = 15 };
        _timer.Tick += OnTick;
    }

    public void ApplyInitialBounds(bool expanded)
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        float scale = _form.DeviceDpi > 0 ? _form.DeviceDpi / 96f : 1f;
        int logicalW = expanded ? ExpandedLogicalWidth : CompactLogicalWidth;
        int w = Math.Min(screen.Width, (int)Math.Round(logicalW * scale));
        int h = Math.Min((int)(screen.Height * 0.94), (int)Math.Round(InitialLogicalHeight * scale));
        _form.Size = new Size(w, h);
        _form.MinimumSize = new Size(
            (int)Math.Round(MinLogicalWidth * scale),
            (int)Math.Round(MinLogicalHeight * scale));
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

    public void Dispose() => _timer.Dispose();
}
