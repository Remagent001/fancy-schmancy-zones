using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace FancySchmancyZones;

/// <summary>
/// A full-screen overlay that shows every layout as a clickable card — the "Pick from cards"
/// double-tap-Ctrl mode. Each card shows the layout's name and a little map of where its windows
/// sit, so they're easy to tell apart. Click a card (or press 1–9) to switch to it; Esc, or
/// clicking away, cancels. It takes focus on purpose (unlike the OsdForm flash) so it can receive
/// the click and the Esc key.
/// </summary>
internal sealed class LayoutPickerForm : Form
{
    private static LayoutPickerForm? _current;   // only ever one open at a time

    /// <summary>Is the picker currently on screen? The tray app uses this to suspend background
    /// flips (Ctrl chords, a pending settle) so nothing rearranges windows behind the open picker.</summary>
    public static bool IsOpen => _current is { IsDisposed: false };

    private readonly Action<int> _onPick;
    private readonly int _count;
    private readonly Label _title;
    private readonly Label _hint;
    private readonly FlowLayoutPanel _flow;

    /// <summary>Show the picker (or re-focus the one already open). onPick fires with the chosen
    /// layout index; nothing fires if the user cancels.</summary>
    public static void Show(IReadOnlyList<LockedLayout> layouts, int currentIndex, Action<int> onPick)
    {
        if (layouts.Count == 0) return;
        if (_current is { IsDisposed: false })
        {
            _current.Activate();
            return;
        }
        var f = new LayoutPickerForm(layouts, currentIndex, onPick);
        _current = f;
        f.FormClosed += (_, _) => { if (ReferenceEquals(_current, f)) _current = null; };
        f.Show();
        f.Activate();
    }

    private LayoutPickerForm(IReadOnlyList<LockedLayout> layouts, int currentIndex, Action<int> onPick)
    {
        _onPick = onPick;
        _count = layouts.Count;

        var screen = Screen.FromPoint(Cursor.Position);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(16, 16, 20);
        Opacity = 0.95;

        _title = new Label
        {
            Text = "Pick a layout",
            Font = new Font("Segoe UI", 22f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true
        };
        _hint = new Label
        {
            Text = "Click a card  ·  press 1–9  ·  Esc to cancel",
            Font = new Font("Segoe UI", 11.5f),
            ForeColor = Color.FromArgb(165, 165, 175),
            BackColor = Color.Transparent,
            AutoSize = true
        };
        // NOTE: AutoSize + AutoScroll on a FlowLayoutPanel fight each other (mis-measures and drops
        // the last card behind a phantom scrollbar). Use AutoSize alone; MaximumSize.Width (set in
        // LayoutContents) drives wrapping, so cards flow into as many rows as needed with no scrollbar.
        _flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            BackColor = Color.Transparent
        };

        for (int i = 0; i < layouts.Count; i++)
        {
            int idx = i;
            var card = new Card(layouts[i], i + 1, i == currentIndex);
            card.Click += (_, _) => Pick(idx);
            _flow.Controls.Add(card);
        }

        Controls.Add(_flow);
        Controls.Add(_title);
        Controls.Add(_hint);

        // Clicking anywhere that isn't a card cancels (backdrop, title, hint, panel gaps). A child
        // control's Click doesn't bubble to the Form, so wire the non-card controls explicitly.
        Click += (_, _) => Close();
        _title.Click += (_, _) => Close();
        _hint.Click += (_, _) => Close();
        _flow.Click += (_, _) => Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutContents();
        // Best-effort only. A tray (background) app is usually DENIED real foreground focus by
        // Windows, so we do NOT depend on focus: Esc and 1–9 are delivered by the app's global
        // keyboard hook (see WantsKey/PressKey), and mouse clicks work on a topmost window anyway.
        BringToFront();
        Activate();
    }

    // If the picker ever does hold focus and then loses it (e.g. a mouse click activated it, then
    // the user clicked another app), don't leave a full-screen overlay stranded — cancel.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!IsDisposed && !Disposing) Close();
    }

    // ---- Keyboard delivered by the app's global hook (works even though we usually lack focus) ----

    private const int VK_ESCAPE = 0x1B, VK_1 = 0x31, VK_9 = 0x39, VK_NUMPAD1 = 0x61, VK_NUMPAD9 = 0x69;

    /// <summary>Does the open picker want this virtual-key? (Esc, or 1–9 / numpad 1–9.)</summary>
    public static bool WantsKey(int vk) =>
        IsOpen && (vk == VK_ESCAPE || (vk >= VK_1 && vk <= VK_9) || (vk >= VK_NUMPAD1 && vk <= VK_NUMPAD9));

    /// <summary>Act on a key the global hook routed to us (call on key-DOWN). Marshals to the UI thread.</summary>
    public static void PressKey(int vk)
    {
        var f = _current;
        if (f is null || f.IsDisposed) return;
        try
        {
            if (vk == VK_ESCAPE) { f.BeginInvoke((Action)f.Close); return; }
            int n = vk >= VK_NUMPAD1 ? vk - VK_NUMPAD1 : vk - VK_1;
            f.BeginInvoke((Action)(() => { if (n >= 0 && n < f._count) f.Pick(n); }));
        }
        catch { /* form may have just closed on another thread */ }
    }

    private void LayoutContents()
    {
        int availW = ClientSize.Width, availH = ClientSize.Height;
        _flow.MaximumSize = new Size(availW - 120, 0);   // wrap by width only; never clip vertically
        var flowSize = _flow.PreferredSize;
        _flow.Size = flowSize;

        int blockH = _title.Height + 10 + flowSize.Height + 10 + _hint.Height;
        int top = Math.Max(24, (availH - blockH) / 2);

        _title.Location = new Point((availW - _title.Width) / 2, top);
        _flow.Location = new Point(Math.Max(0, (availW - flowSize.Width) / 2), _title.Bottom + 10);
        _hint.Location = new Point((availW - _hint.Width) / 2, _flow.Bottom + 10);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { Close(); return; }

        int n = e.KeyCode switch
        {
            >= Keys.D1 and <= Keys.D9 => e.KeyCode - Keys.D1,
            >= Keys.NumPad1 and <= Keys.NumPad9 => e.KeyCode - Keys.NumPad1,
            _ => -1
        };
        if (n >= 0 && n < _count) Pick(n);
    }

    // Close BEFORE arranging, so the overlay is gone by the time the windows shuffle.
    private void Pick(int idx) { Close(); _onPick(idx); }

    /// <summary>One layout tile: name, a little scaled map of its window rectangles, and a number.</summary>
    private sealed class Card : Panel
    {
        private readonly LockedLayout _layout;
        private readonly int _number;      // 1-based; the keyboard shortcut. 0 = none.
        private readonly bool _isCurrent;
        private bool _hover;

        public Card(LockedLayout layout, int number, bool isCurrent)
        {
            _layout = layout;
            _number = number;
            _isCurrent = isCurrent;
            Size = new Size(300, 210);
            Margin = new Padding(14);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(34, 34, 40);
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color borderColor = _hover ? Color.FromArgb(120, 175, 255)
                              : _isCurrent ? Color.FromArgb(95, 135, 205)
                              : Color.FromArgb(70, 70, 82);
            using (var pen = new Pen(borderColor, _hover || _isCurrent ? 2f : 1f))
                g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);

            using (var titleFont = new Font("Segoe UI", 12f, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(Color.White))
            using (var sf = new StringFormat(StringFormatFlags.LineLimit) { Trimming = StringTrimming.EllipsisWord })
                g.DrawString(_layout.Name, titleFont, titleBrush, new RectangleF(12, 9, Width - 46, 42), sf);

            if (_number is >= 1 and <= 9)
                using (var numFont = new Font("Segoe UI", 11.5f, FontStyle.Bold))
                using (var numBrush = new SolidBrush(Color.FromArgb(150, 150, 162)))
                    g.DrawString(_number.ToString(), numFont, numBrush, Width - 28, 9);

            DrawMiniMap(g, new Rectangle(14, 56, Width - 28, Height - 70), _layout);
        }

        /// <summary>Draw the layout's window rectangles, scaled to fit the given area (aspect
        /// preserved, centered) — a recognizable little "map" of the arrangement.</summary>
        private static void DrawMiniMap(Graphics g, Rectangle area, LockedLayout layout)
        {
            var wins = layout.Windows;
            if (wins.Count == 0)
            {
                using var f = new Font("Segoe UI", 9.5f, FontStyle.Italic);
                using var b = new SolidBrush(Color.FromArgb(120, 120, 130));
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("(no windows saved)", f, b, area, sf);
                return;
            }

            int minX = wins.Min(w => w.Bounds.X);
            int minY = wins.Min(w => w.Bounds.Y);
            int maxX = wins.Max(w => w.Bounds.X + w.Bounds.W);
            int maxY = wins.Max(w => w.Bounds.Y + w.Bounds.H);
            float spanW = Math.Max(1, maxX - minX);
            float spanH = Math.Max(1, maxY - minY);
            float scale = Math.Min(area.Width / spanW, area.Height / spanH);
            float drawW = spanW * scale, drawH = spanH * scale;
            float offX = area.X + (area.Width - drawW) / 2f;
            float offY = area.Y + (area.Height - drawH) / 2f;

            using var fill = new SolidBrush(Color.FromArgb(70, 120, 170, 255));
            using var pen = new Pen(Color.FromArgb(210, 150, 190, 255), 1f);
            foreach (var w in wins)
            {
                float x = offX + (w.Bounds.X - minX) * scale;
                float y = offY + (w.Bounds.Y - minY) * scale;
                float ww = Math.Max(3, w.Bounds.W * scale);
                float hh = Math.Max(3, w.Bounds.H * scale);
                g.FillRectangle(fill, x, y, ww, hh);
                g.DrawRectangle(pen, x, y, ww, hh);
            }
        }
    }
}
