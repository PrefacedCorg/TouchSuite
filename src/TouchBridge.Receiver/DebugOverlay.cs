using System.Drawing;
using System.Windows.Forms;

namespace TouchBridge.Receiver;

/// <summary>
/// 屏幕左上角的调试小窗：显示从平板收到的实时数据。
/// 置顶 + 鼠标穿透（WS_EX_TRANSPARENT），不会遮挡注入到左上角的触摸。
/// </summary>
internal sealed class DebugOverlay : IDisposable
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly Func<string> _textProvider;
    private readonly Action<string>? _log;
    private Thread? _thread;
    private Form? _form;
    private volatile bool _disposed;

    public DebugOverlay(Func<string> textProvider, Action<string>? log = null)
    {
        _textProvider = textProvider;
        _log = log;
    }

    public void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "TouchBridge-DebugOverlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            var label = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(180, 255, 190),
                BackColor = Color.Transparent,
                Font = new Font("Consolas", 9.5f),
                Padding = new Padding(2),
                Text = "TouchBridge",
            };

            var form = new OverlayForm
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(8, 8),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                TopMost = true,
                ShowInTaskbar = false,
                BackColor = Color.Black,
                Opacity = 0.72,
                Padding = new Padding(10, 8, 14, 8),
                Text = "TouchBridge 调试",
            };
            form.Controls.Add(label);
            _form = form;

            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (_, _) => label.Text = _textProvider();
            form.Shown += (_, _) =>
            {
                label.Text = _textProvider();
                timer.Start();
                _log?.Invoke($"调试小窗已显示：位置 ({form.Left},{form.Top})，尺寸 {form.Width}x{form.Height}（鼠标穿透，不挡触摸）");
            };

            Application.Run(form);

            timer.Stop();
            timer.Dispose();
        }
        catch (Exception ex)
        {
            // 无法创建调试窗口时不影响转发功能，但要让人知道为什么没看到小窗
            _log?.Invoke($"调试小窗创建失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            Form? f = _form;
            if (f is { IsDisposed: false })
                f.BeginInvoke(new Action(() => f.Close()));
        }
        catch
        {
            // 忽略关闭阶段异常
        }

        _thread?.Join(800);
    }

    /// <summary>置顶 + 不激活 + 鼠标穿透的窗口。</summary>
    private sealed class OverlayForm : Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }
    }
}
