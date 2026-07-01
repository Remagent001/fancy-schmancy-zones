using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

    public static void MoveTo(IntPtr hwnd, Rect b)
    {
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.W, b.H, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
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

    /// <summary>Launch a program by its .exe path. Returns true if it started.</summary>
    public static bool Launch(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
}
