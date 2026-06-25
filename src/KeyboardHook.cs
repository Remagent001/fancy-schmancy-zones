using System.Runtime.InteropServices;

namespace FancySchmancyZones;

/// <summary>
/// A global low-level keyboard hook. Unlike RegisterHotKey, this sees keystrokes
/// directly, so it still works when another utility has "claimed" combos.
/// The handler returns true to swallow the key (stop it reaching other apps).
///
/// The hook runs on its OWN dedicated thread with its own message loop. This is
/// critical: a low-level keyboard hook callback must return quickly, and if it ran
/// on the UI thread it could be blocked whenever the UI thread does slow window
/// work — which stalls keyboard input system-wide and makes Windows kill us with an
/// "app hang." On a dedicated thread the hook is always serviced instantly; it only
/// posts the real work to the UI thread.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>Handle a key event. Return true to swallow the key. Keep it FAST.</summary>
    public delegate bool KeyHandler(int vkCode, bool keyDown);

    private readonly KeyHandler _onKey;
    private readonly LowLevelKeyboardProc _proc; // keep the delegate alive for the hook's lifetime
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hook = IntPtr.Zero;
    private uint _threadId;
    private volatile bool _installed;

    public KeyboardHook(KeyHandler onKey)
    {
        _onKey = onKey;
        _proc = Callback;
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "FSZ-KeyboardHook" };
        _thread.Start();
        _ready.Wait(3000); // wait until the hook is installed (or failed) on its thread
    }

    public bool Installed => _installed;

    private void ThreadProc()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        _installed = _hook != IntPtr.Zero;
        _ready.Set();

        if (!_installed) return;

        // Pump messages so the hook keeps being serviced. WM_QUIT (from Dispose) ends it.
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            // Nothing to dispatch for an LL hook; just keep the queue moving.
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (down || up)
            {
                int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field
                bool swallow = false;
                try { swallow = _onKey(vk, down); } catch { /* never let a handler error stall input */ }
                if (swallow) return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Is a key currently held down? (Ctrl/Alt/Shift etc.)</summary>
    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        try { _thread.Join(1500); } catch { }
        _ready.Dispose();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
