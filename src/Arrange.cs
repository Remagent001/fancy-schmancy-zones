using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FancySchmancyZones;

/// <summary>
/// "Arrange windows" — the Window Cascade features, rebuilt natively (2026-07-11 merge).
/// Arranging is TRANSIENT by design: it never creates, updates, or touches a layout. The
/// workflow is: arrange → move/minimize things by hand → then explicitly Lock a new layout
/// or right-click-update an existing one. A flip is allowed to wipe an arrangement — it's
/// not a layout unless the user asked for one.
/// </summary>
public static class Arrange
{
    public enum Shape { Grid, SideBySide, Stacked, Cascade }

    // Visual gap between tiled windows: each interior edge gives up half.
    private const int Gap = 16;
    private const int CascadeStep = 30;   // diagonal offset per window when cascading

    // ---- Geometry (pure math; work area in, window rectangles out) ----

    public static List<Rect> Compute(Shape shape, Rectangle work, int n)
    {
        var rects = new List<Rect>(Math.Max(n, 0));
        if (n <= 0) return rects;
        switch (shape)
        {
            case Shape.Grid:
                // Squarish grid: cols = ceil(sqrt(n)). Cell edges use integer division so the
                // cells tile the work area exactly, with no rounding drift on the last row/column.
                int cols = (int)Math.Ceiling(Math.Sqrt(n));
                int rows = (int)Math.Ceiling(n / (double)cols);
                for (int i = 0; i < n; i++) rects.Add(Cell(work, cols, rows, i % cols, i / cols));
                break;

            case Shape.SideBySide:
                for (int i = 0; i < n; i++) rects.Add(Cell(work, n, 1, i, 0));
                break;

            case Shape.Stacked:
                for (int i = 0; i < n; i++) rects.Add(Cell(work, 1, n, 0, i));
                break;

            case Shape.Cascade:
                // Each window 70% of the work area, stepping down-right; if the diagonal would
                // run off the edge, wrap back to a small stagger so every title bar stays visible.
                int w = work.Width * 7 / 10, h = work.Height * 7 / 10;
                for (int i = 0; i < n; i++)
                {
                    int x = work.X + CascadeStep * i, y = work.Y + CascadeStep * i;
                    if (x + w > work.Right) x = work.X + (i % 5) * CascadeStep;
                    if (y + h > work.Bottom) y = work.Y + (i % 5) * CascadeStep;
                    rects.Add(new Rect(x, y, w, h));
                }
                break;
        }
        return rects;
    }

    private static Rect Cell(Rectangle work, int cols, int rows, int col, int row)
    {
        int x0 = work.X + col * work.Width / cols;
        int x1 = work.X + (col + 1) * work.Width / cols;
        int y0 = work.Y + row * work.Height / rows;
        int y1 = work.Y + (row + 1) * work.Height / rows;
        int l = col > 0 ? Gap / 2 : 0, r = col < cols - 1 ? Gap / 2 : 0;
        int t = row > 0 ? Gap / 2 : 0, b = row < rows - 1 ? Gap / 2 : 0;
        return new Rect(x0 + l, y0 + t, x1 - x0 - l - r, y1 - y0 - t - b);
    }

    // ---- Doing an arrangement ----

    /// <summary>
    /// Arrange the group's windows. spreadAcrossMonitors=true splits the group evenly across
    /// monitors left-to-right (per-app arranges); false keeps each window on the monitor it's
    /// already on and lays out each monitor's share there (All windows). Runs on a background
    /// thread. Returns how many windows were placed.
    /// </summary>
    public static int Do(Shape shape, List<LiveWindow> group, bool spreadAcrossMonitors)
    {
        if (group.Count == 0) return 0;

        // Un-park first: a minimized window's coordinates AND its invisible-frame measurements
        // are garbage until it's restored, and "show all my X windows" should include the ones
        // that were put away. Maximized windows also need restoring, or moving them just
        // changes where they'd LATER un-maximize to.
        bool anyRestored = false;
        foreach (var w in group)
        {
            if (IsIconic(w.Hwnd) || IsZoomed(w.Hwnd))
            {
                ShowWindowAsync(w.Hwnd, SW_RESTORE);
                anyRestored = true;
            }
        }
        if (anyRestored) System.Threading.Thread.Sleep(300);   // let them un-park before measuring

        // Windows we can't actually place get dropped BEFORE the grid is sized, so they never
        // leave an empty cell: ones that closed since the menu was read, and elevated (admin)
        // windows — we're not allowed to touch those, so one that refused to un-minimize above
        // is still iconic here and stays put. (A visible admin window can't be detected cheaply;
        // it just won't move, and the caller reports the honest count.)
        group = group.Where(w => WindowManager.IsAlive(w.Hwnd) && !IsIconic(w.Hwnd)).ToList();
        if (group.Count == 0) return 0;

        var monitors = Screen.AllScreens.OrderBy(s => s.Bounds.X).ToList();
        var chunks = new List<(Screen Mon, List<LiveWindow> Wins)>();

        if (spreadAcrossMonitors && monitors.Count > 1)
        {
            // Even split, extras to the leftmost monitors: 7 windows on 2 monitors = 4 + 3.
            int m = monitors.Count, baseCount = group.Count / m, extra = group.Count % m, at = 0;
            for (int i = 0; i < m; i++)
            {
                int take = baseCount + (i < extra ? 1 : 0);
                if (take > 0) chunks.Add((monitors[i], group.Skip(at).Take(take).ToList()));
                at += take;
            }
        }
        else if (!spreadAcrossMonitors && monitors.Count > 1)
        {
            foreach (var g in group.GroupBy(w => MonitorFor(w.Hwnd, monitors)))
                chunks.Add((g.Key, g.ToList()));
        }
        else
        {
            chunks.Add((monitors[0], group));
        }

        int moved = 0;
        foreach (var (mon, wins) in chunks)
        {
            var rects = Compute(shape, mon.WorkingArea, wins.Count);
            for (int i = 0; i < wins.Count; i++)
            {
                if (MoveFlush(wins[i].Hwnd, rects[i]))   // honest count: only what actually moved
                    moved++;
            }
        }

        // Second pass: when a window lands on a monitor with DIFFERENT display scaling, Windows
        // rescales it right after our move (WM_DPICHANGED), mangling the size we just set —
        // verified live: spread-to-monitor-2 windows kept their old size. Re-applying the same
        // rect once the window is already ON its target monitor sticks, because no DPI change
        // fires the second time. (Same double-apply trick PowerToys FancyZones uses. Insets are
        // re-measured fresh in MoveFlush — the frame thickness differs per DPI too.)
        System.Threading.Thread.Sleep(350);
        foreach (var (mon, wins) in chunks)
        {
            var rects = Compute(shape, mon.WorkingArea, wins.Count);
            for (int i = 0; i < wins.Count; i++)
                MoveFlush(wins[i].Hwnd, rects[i]);
        }

        // Raise back-to-front so the window that was frontmost when the menu was opened ends up
        // on top — same deterministic-stacking rule the flip uses (v0.10.4).
        for (int i = group.Count - 1; i >= 0; i--) WindowManager.RaiseToTop(group[i].Hwnd);
        WindowManager.Focus(group[0].Hwnd);
        return moved;
    }

    /// <summary>The monitor a window lives on: the one containing its center (using the restore
    /// position if it's minimized/parked), else the nearest one.</summary>
    private static Screen MonitorFor(IntPtr hwnd, List<Screen> monitors)
    {
        Rectangle r = default;
        if (GetWindowRect(hwnd, out RECT wr))
            r = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);

        // Parked/off-screen (e.g. still mid-restore)? Use where it would restore to instead.
        if (!monitors.Any(m => m.Bounds.IntersectsWith(r)))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp))
            {
                var n = wp.rcNormalPosition;
                r = new Rectangle(n.Left, n.Top, n.Right - n.Left, n.Bottom - n.Top);
            }
        }

        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        foreach (var m in monitors)
            if (m.Bounds.Contains(c)) return m;
        return monitors.OrderBy(m =>
            Math.Abs(m.Bounds.X + m.Bounds.Width / 2 - c.X) +
            Math.Abs(m.Bounds.Y + m.Bounds.Height / 2 - c.Y)).First();
    }

    /// <summary>
    /// Move a window so its VISIBLE edges land exactly on the target rectangle. Windows 10/11
    /// windows carry an invisible resize/shadow border (~7px each side); moving to the raw
    /// rectangle leaves phantom gaps between tiled windows. Measures the visible frame via
    /// DWMWA_EXTENDED_FRAME_BOUNDS and expands the target by the difference. All placement
    /// still goes through WindowManager.MoveTo (off-screen guard, async, non-blocking).
    /// </summary>
    public static bool MoveFlush(IntPtr hwnd, Rect target)
    {
        var t = target;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT f, Marshal.SizeOf<RECT>()) == 0 &&
            GetWindowRect(hwnd, out RECT wr))
        {
            // Sanity-clamp each inset: a bogus measurement must never fling a window around.
            int l = Ins(f.Left - wr.Left), tp = Ins(f.Top - wr.Top);
            int r = Ins(wr.Right - f.Right), b = Ins(wr.Bottom - f.Bottom);
            t = new Rect(target.X - l, target.Y - tp, target.W + l + r, target.H + tp + b);
        }
        return WindowManager.MoveTo(hwnd, t);

        static int Ins(int v) => Math.Clamp(v, -2, 20);
    }

    // ---- One-level undo ----

    private static readonly object _undoLock = new();
    // Ordered front-first (the order the caller enumerated, which is z-order): undo restores
    // stacking from it, so it must stay a list, not a dictionary.
    private static List<(IntPtr Hwnd, WINDOWPLACEMENT Wp)> _undo = new();

    public static bool HasUndo { get { lock (_undoLock) return _undo.Count > 0; } }

    /// <summary>Remember where every touched window is BEFORE an arrange — position, stacking
    /// order, and whether it was minimized/maximized — so one Undo puts it all back. Pass the
    /// windows FRONT-FIRST (z-order). Each arrange replaces the previous snapshot (single-level,
    /// same as Window Cascade).</summary>
    public static void SnapshotForUndo(IEnumerable<IntPtr> hwnds)
    {
        var snap = new List<(IntPtr, WINDOWPLACEMENT)>();
        var seen = new HashSet<IntPtr>();
        foreach (var h in hwnds)
        {
            if (!seen.Add(h)) continue;
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(h, ref wp)) snap.Add((h, wp));
        }
        lock (_undoLock) _undo = snap;
    }

    /// <summary>Put every window from the last snapshot back (skips ones closed since):
    /// position, size, minimized/maximized state, AND stacking order. Returns how many were
    /// restored. Clears the snapshot.</summary>
    public static int UndoLast()
    {
        List<(IntPtr Hwnd, WINDOWPLACEMENT Wp)> snap;
        lock (_undoLock) { snap = _undo; _undo = new(); }

        int n = 0;
        foreach (var (h, saved) in snap)
        {
            if (ApplyPlacement(h, saved)) n++;
        }

        // Second pass, same reason as Do(): a window restored ACROSS a display-scaling boundary
        // gets rescaled by Windows right after it lands, mangling the size we set. Re-applying
        // once it's back on its home monitor sticks.
        if (n > 0)
        {
            System.Threading.Thread.Sleep(350);
            foreach (var (h, saved) in snap) ApplyPlacement(h, saved);

            // Restore stacking: raise back-to-front so the window that was frontmost before the
            // arrange ends up on top again — but never touch ones that were (and should stay)
            // minimized: RaiseToTop would un-minimize them.
            for (int i = snap.Count - 1; i >= 0; i--)
            {
                var (h, saved) = snap[i];
                if (WindowManager.IsAlive(h) && saved.showCmd != SW_SHOWMINIMIZED)
                    WindowManager.RaiseToTop(h);
            }
            foreach (var (h, saved) in snap)
            {
                if (WindowManager.IsAlive(h) && saved.showCmd != SW_SHOWMINIMIZED)
                {
                    WindowManager.Focus(h);
                    break;
                }
            }
        }
        return n;

        static bool ApplyPlacement(IntPtr h, WINDOWPLACEMENT saved)
        {
            if (!WindowManager.IsAlive(h)) return false;
            var wp = saved;
            wp.length = Marshal.SizeOf<WINDOWPLACEMENT>();
            wp.flags |= WPF_ASYNCWINDOWPLACEMENT;   // never block on a busy app
            return SetWindowPlacement(h, ref wp);
        }
    }

    // ---- P/Invoke (private to arranging; flip/lock plumbing in WindowManager is untouched) ----

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int SW_RESTORE = 9;
    private const int SW_SHOWMINIMIZED = 2;
    private const int WPF_ASYNCWINDOWPLACEMENT = 0x0004;
}
