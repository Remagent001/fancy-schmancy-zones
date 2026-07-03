using System.Drawing;
using System.Windows.Forms;

namespace FancySchmancyZones;

/// <summary>
/// A quick on-screen flash (like the volume popup) that shows which layout you're on while
/// cycling with double-tap Ctrl. It never takes focus or keyboard input, and hides itself
/// shortly after the last update. This is what lets rapid cycling feel instant: the name
/// updates immediately on every tap, and the actual window shuffling waits until you land.
/// </summary>
internal sealed class OsdForm : Form
{
    private static OsdForm? _instance;
    private readonly FlowLayoutPanel _flow;
    private readonly Label _label;
    private readonly Label _sub;
    private readonly System.Windows.Forms.Timer _hide;

    private OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(32, 32, 36);

        _label = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 26f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        // Small status line under the name — e.g. "5 of 9 windows — the rest aren't open".
        // This replaces Windows notifications for flips entirely: toasts queue up and dribble
        // out one every few seconds, while this appears instantly and vanishes on its own.
        _sub = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 12f),
            ForeColor = Color.FromArgb(195, 195, 205),
            BackColor = Color.Transparent,
            Margin = new Padding(2, 8, 0, 0),
            Visible = false
        };

        _flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(36, 20, 36, 24),
            BackColor = Color.Transparent,
            Location = new Point(0, 0)
        };
        _flow.Controls.Add(_label);
        _flow.Controls.Add(_sub);
        Controls.Add(_flow);

        _hide = new System.Windows.Forms.Timer { Interval = 1100 };
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
    }

    // Never steal focus from whatever the user is typing in.
    protected override bool ShowWithoutActivation => true;

    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>Show (or refresh) the flash: big layout name, optional small status line
    /// under it. Call on the UI thread. With a status line it lingers a bit longer.</summary>
    public static void Flash(string text, string? sub = null)
    {
        _instance ??= new OsdForm();
        var f = _instance;
        f._label.Text = text;
        f._sub.Text = sub ?? "";
        f._sub.Visible = sub != null;
        f.ClientSize = f._flow.PreferredSize;   // the panel's padding gives the box its margins

        var wa = Screen.PrimaryScreen!.WorkingArea;
        f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + wa.Height / 4);

        if (!f.Visible) f.Show();
        f._hide.Stop();
        f._hide.Interval = sub != null ? 2600 : 1100;
        f._hide.Start();
    }
}
