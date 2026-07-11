using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FancySchmancyZones;

/// <summary>
/// A live, top-level window that shows up in Alt+Tab.
/// </summary>
public sealed class LiveWindow
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; } = "";
    public string Process { get; init; } = "";
    public string ExePath { get; init; } = "";

    // Chrome/Edge profile folder (e.g. "Profile 10"). Filled in on demand, not during
    // enumeration, so plain window listing stays cheap. Empty for non-browser windows.
    public string Profile { get; set; } = "";

    public Rect Bounds { get; init; }
}

/// <summary>
/// All the Win32 plumbing for finding, moving, and focusing windows.
/// FancyZones handles the snapping; this just remembers and replays where things are.
/// </summary>
public static class WindowManager
{
    // ---- Enumeration ----

    public static List<LiveWindow> GetAltTabWindows()
    {
        var result = new List<LiveWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsAltTabWindow(hwnd)) return true;

            int len = GetWindowTextLength(hwnd);
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);

            GetWindowThreadProcessId(hwnd, out uint pid);
            string proc = "";
            string exe = "";
            try
            {
                var p = Process.GetProcessById((int)pid);
                proc = p.ProcessName;
                try { exe = p.MainModule?.FileName ?? ""; } catch { /* elevated/system procs deny module access */ }
            }
            catch { }

            GetWindowRect(hwnd, out RECT r);

            result.Add(new LiveWindow
            {
                Hwnd = hwnd,
                Title = sb.ToString(),
                Process = proc,
                ExePath = exe,
                Bounds = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsAltTabWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;

        // Skip windows DWM has cloaked (e.g. on another virtual desktop, or suspended UWP).
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_APPWINDOW) != 0) return true;          // explicitly app window
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false; // owned -> not in alt-tab
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;        // tool window -> not in alt-tab
        return true;
    }

    // ---- Movement & focus ----

    // IMPORTANT: every cross-process window call below is NON-BLOCKING. We use
    // ShowWindowAsync (posts, never waits) and SWP_ASYNCWINDOWPOS, and we do NOT use
    // AttachThreadInput. Attaching our input queue to another app's (e.g. Outlook) and
    // any synchronous Set* call can deadlock when that app is busy — which showed up as
    // a Windows "app hang." Best-effort and safe beats forceful and frozen.

    /// <summary>Returns whether the move was actually issued and accepted — flip callers ignore
    /// this (behavior unchanged); the Arrange feature uses it to report an honest count (e.g. an
    /// elevated/admin window silently refuses moves from a non-admin app).</summary>
    public static bool MoveTo(IntPtr hwnd, Rect b)
    {
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        // Never move a window to a spot that's off every monitor — stale layout data (e.g. a
        // position captured while the window was minimized/"parked") would otherwise teleport
        // it into the void: still on the taskbar, but impossible to see. Restore + raise only.
        if (!IsOnScreen(b)) return false;
        return SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.W, b.H, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    /// <summary>Raise a window toward the top without stealing focus.</summary>
    public static void RaiseToTop(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    /// <summary>Best-effort bring a window to the foreground (no input-queue attaching — that can hang).</summary>
    public static void Focus(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>Minimize a window (e.g. one that isn't part of the layout being switched to).</summary>
    public static void Minimize(IntPtr hwnd) => ShowWindowAsync(hwnd, SW_MINIMIZE);

    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    /// <summary>True if this rectangle is visible on at least one currently connected monitor.
    /// Minimized windows report a "parked" position tens of thousands of pixels off-screen —
    /// treating that as a real position is how windows get teleported into the void.</summary>
    public static bool IsOnScreen(Rect b) =>
        b.W > 0 && b.H > 0 &&
        Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(new Rectangle(b.X, b.Y, b.W, b.H)));

    /// <summary>
    /// From a Z-ORDERED (front-first) list of windows, keep only the ones you can actually SEE —
    /// i.e. drop any window that's COMPLETELY hidden behind the windows in front of it. A window
    /// peeking out even a sliver is kept (you can see it, so you meant it to be in the arrangement).
    /// This is what makes "Lock" save the layout that's visible on screen, rather than every window
    /// that merely happens to be open behind something. Walks front→back, accumulating the covered
    /// area; a window survives if any part of it is still uncovered when its turn comes.
    /// </summary>
    public static List<LiveWindow> VisibleOnly(IReadOnlyList<LiveWindow> frontToBack)
    {
        var kept = new List<LiveWindow>();
        using var identity = new System.Drawing.Drawing2D.Matrix();
        using var covered = new Region();
        covered.MakeEmpty();   // a fresh Region is INFINITE; start from nothing covered
        foreach (var w in frontToBack)
        {
            var r = new Rectangle(w.Bounds.X, w.Bounds.Y, w.Bounds.W, w.Bounds.H);
            using (var visible = new Region(r))
            {
                visible.Exclude(covered);                           // the part of w not hidden by windows in front
                if (visible.GetRegionScans(identity).Length > 0)    // any pixels left ⇒ at least partly visible
                    kept.Add(w);
            }
            covered.Union(r);                                       // w now hides whatever's behind it
        }
        return kept;
    }

    /// <summary>
    /// Minimize every window in the list, then verify — a busy window (e.g. a terminal mid-output)
    /// can occasionally miss the first "please minimize" message, since we deliberately never block
    /// waiting on another app. Retries a few times; anything still refusing (custom minimize
    /// handling, or an elevated window we can't touch) is pushed to the bottom of the z-order so
    /// it can't cover the layout. Returns those stubborn windows so the caller can log them.
    /// Windows already minimized are left alone (never re-poked) and, for every window touched,
    /// its saved "restore to" position is checked first so it can never come back invisible later.
    /// </summary>
    public static List<IntPtr> MinimizeAll(IEnumerable<IntPtr> hwnds)
    {
        var all = hwnds.Where(IsAlive).ToList();
        foreach (var h in all) EnsureOnScreen(h);

        var remaining = all.Where(h => !IsIconic(h)).ToList();
        for (int attempt = 0; attempt < 5 && remaining.Count > 0; attempt++)
        {
            foreach (var h in remaining) Minimize(h);
            System.Threading.Thread.Sleep(150);
            remaining = remaining.Where(h => IsAlive(h) && !IsIconic(h)).ToList();
        }

        // Some windows refuse to minimize no matter how often we ask — apps with custom
        // minimize handling, or elevated (admin) windows we aren't allowed to poke. Don't
        // let them keep covering the layout: push them to the very bottom of the stack.
        foreach (var h in remaining)
            SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        return remaining;
    }

    /// <summary>
    /// If a window's saved "restore to" position (where it reappears when un-minimized or
    /// un-maximized) is off every currently connected monitor — e.g. left over from a docking
    /// station or monitor that's no longer attached — move it back onto the primary screen.
    /// Without this, a minimized window like that can look "stuck": the taskbar shows it and
    /// clicking it does nothing visible, because it restores to a spot that no longer exists.
    /// Returns true if it had to fix anything.
    /// </summary>
    public static bool EnsureOnScreen(IntPtr hwnd)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return false;

        // Check both where the window IS right now and where it would RESTORE to. A window can
        // be "normal" yet sitting at the minimized-park coordinates (tens of thousands of pixels
        // off-screen) — on the taskbar, but impossible to see. Skip actually-minimized windows'
        // current rect (parked by design); their restore rect is what matters.
        bool currentBad = !IsIconic(hwnd) && GetWindowRect(hwnd, out RECT cur) &&
                          !IsOnScreen(new Rect(cur.Left, cur.Top, cur.Right - cur.Left, cur.Bottom - cur.Top));

        var r = wp.rcNormalPosition;
        var rect = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        bool normalBad = rect.Width > 0 && rect.Height > 0 &&
                         !Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(rect));

        if (!currentBad && !normalBad) return false;   // already fine

        var work = Screen.PrimaryScreen!.WorkingArea;
        int w = Math.Clamp(rect.Width, 400, work.Width - 40);
        int h = Math.Clamp(rect.Height, 300, work.Height - 40);
        wp.rcNormalPosition = new RECT
        {
            Left = work.Left + 20, Top = work.Top + 20,
            Right = work.Left + 20 + w, Bottom = work.Top + 20 + h
        };
        wp.showCmd = SW_SHOWNORMAL;
        SetWindowPlacement(hwnd, ref wp);
        // Belt and braces: placement alone can be ignored by some apps' custom window handling.
        SetWindowPos(hwnd, IntPtr.Zero, work.Left + 20, work.Top + 20, w, h,
                     SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        return true;
    }

    /// <summary>True for Chromium browsers whose windows are split across profiles.</summary>
    public static bool IsChromium(string proc) =>
        proc.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
        proc.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
        proc.Equals("brave", StringComparison.OrdinalIgnoreCase);

    // Terminal/console host apps. Launching one blank doesn't reproduce what was actually
    // running inside it (which folder, which command) — so by default these are matched
    // and repositioned if already open, but not auto-launched.
    private static readonly string[] TerminalHostProcesses =
        { "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "wt" };

    public static bool IsTerminalHost(string proc) =>
        TerminalHostProcesses.Any(p => p.Equals(proc, StringComparison.OrdinalIgnoreCase));

    /// <summary>The command-line switch to open a specific browser profile ("" if none).</summary>
    public static string ProfileArgs(string profile) =>
        string.IsNullOrEmpty(profile) ? "" : $"--profile-directory=\"{profile}\"";

    /// <summary>Fill in the browser profile for each Chromium window in the list (via UI Automation).</summary>
    public static void FillProfiles(List<LiveWindow> live)
    {
        foreach (var w in live)
            if (IsChromium(w.Process))
                w.Profile = BrowserProfiles.DetectFolder(w.Hwnd, w.Process);
    }

    /// <summary>Launch a program by its .exe path, optionally with arguments. Returns true if it started.</summary>
    public static bool Launch(string exePath, string args = "")
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = true,               // let Windows handle it like a double-click
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
            });
            return true;
        }
        catch { return false; }
    }

    // ---- P/Invoke ----

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT lpwndpl);

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

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_SHOWNORMAL = 1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
}
