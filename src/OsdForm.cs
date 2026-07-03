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
    private readonly Label _label;
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
            Padding = new Padding(36, 22, 36, 26)
        };
        Controls.Add(_label);

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

    /// <summary>Show (or refresh) the flash with this text. Call on the UI thread.</summary>
    public static void Flash(string text)
    {
        _instance ??= new OsdForm();
        var f = _instance;
        f._label.Text = text;
        f.ClientSize = f._label.PreferredSize;   // the label's padding gives the box its margins

        var wa = Screen.PrimaryScreen!.WorkingArea;
        f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + wa.Height / 4);

        if (!f.Visible) f.Show();
        f._hide.Stop();
        f._hide.Start();
    }
}
