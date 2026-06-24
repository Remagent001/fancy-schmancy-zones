using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FancySchmancyZones;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single instance: if already running, just exit quietly.
        using var mutex = new Mutex(true, "FancySchmancyZones_SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

/// <summary>
/// The whole app lives here: a tray icon, a hidden window that catches global
/// hotkeys, and the logic to lock / switch between layouts.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly KeyboardHook _hook;
    private readonly Control _sync = new();   // marshals hook-thread work back onto the UI message loop
    private readonly AppState _state;
    private ContextMenuStrip _menu = new();
    private int _currentIndex = -1;

    // The chords we listen for (fixed; the low-level hook sees them regardless of what else grabs keys).
    private readonly string _lockKeyLabel = "Ctrl+Alt+Shift+L";
    private readonly string _nextKeyLabel = "Ctrl+Alt+Shift+Q";
    private readonly string _prevKeyLabel = "Ctrl+Alt+Shift+W";

    public TrayContext()
    {
        _state = AppState.Load();

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Fancy Schmancy Zones",
            Visible = true
        };
        // Left-click OR right-click the tray icon opens the menu.
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _menu.Show(Cursor.Position); };
        RebuildMenu();

        _ = _sync.Handle; // force the handle to exist so BeginInvoke actually works

        // Low-level keyboard hook: sees keys directly, even when other utilities
        // have grabbed combos via RegisterHotKey.
        _hook = new KeyboardHook(OnKey);

        try
        {
            Directory.CreateDirectory(AppState.Dir);
            File.WriteAllText(Path.Combine(AppState.Dir, "hotkeys.txt"),
                $"Listener installed: {_hook.Installed}\nLock: {_lockKeyLabel}\nNext: {_nextKeyLabel}\nPrev: {_prevKeyLabel}\nFlip (easy): double-tap Ctrl\n");
        }
        catch { }

        if (!_hook.Installed)
            Notify("Hotkeys unavailable", "Couldn't start the keyboard listener — use the tray icon to switch layouts.");
        else
            Notify("Fancy Schmancy Zones is running",
                $"Flip layouts: double-tap Ctrl.\nLock: {_lockKeyLabel} · Next: {_nextKeyLabel} · Prev: {_prevKeyLabel}");
    }

    // --- Double-tap Ctrl detection state ---
    private const long DoubleTapMs = 400;   // max gap between the two Ctrl taps
    private long _lastCtrlTapTick;
    private bool _ctrlHeld;
    private bool _comboDuringCtrl;          // was Ctrl part of a combo (so it's not a clean tap)?
    private bool _diagWritten;

    /// <summary>
    /// Called from the keyboard hook for every key down/up. Detects:
    ///   • double-tap of Ctrl  -> flip to next layout (easy, hard to block)
    ///   • Ctrl+Alt+Shift+L/Q/W chords -> lock / next / previous
    /// Must return fast; heavy work is marshalled to the UI thread.
    /// </summary>
    private bool OnKey(int vk, bool down)
    {
        // First key we ever see proves the listener is actually receiving input on this PC.
        if (!_diagWritten)
        {
            _diagWritten = true;
            try { File.WriteAllText(Path.Combine(AppState.Dir, "diag.txt"), "keyboard listener IS receiving keys\n"); } catch { }
        }

        bool isCtrl = vk == VK_LCONTROL || vk == VK_RCONTROL;

        if (isCtrl)
        {
            if (down)
            {
                if (!_ctrlHeld) { _ctrlHeld = true; _comboDuringCtrl = false; } // ignore auto-repeat
            }
            else // Ctrl released — a "tap" completes here
            {
                _ctrlHeld = false;
                if (_comboDuringCtrl) { _lastCtrlTapTick = 0; } // it was a combo, not a clean tap
                else
                {
                    long now = Environment.TickCount64;
                    if (_lastCtrlTapTick != 0 && now - _lastCtrlTapTick <= DoubleTapMs)
                    {
                        _lastCtrlTapTick = 0;
                        Run(() => Cycle(+1));   // double-tap! flip to next layout
                    }
                    else _lastCtrlTapTick = now;
                }
            }
            return false; // never swallow Ctrl — it's used everywhere
        }

        // Any non-Ctrl key:
        if (down)
        {
            if (_ctrlHeld) _comboDuringCtrl = true;   // Ctrl is being used in a combo
            _lastCtrlTapTick = 0;                     // breaks any pending double-tap

            if (KeyboardHook.IsDown(VK_CONTROL) && KeyboardHook.IsDown(VK_MENU) && KeyboardHook.IsDown(VK_SHIFT))
            {
                Action? action = (uint)vk switch
                {
                    VK_L => LockCurrent,
                    VK_Q => () => Cycle(+1),
                    VK_W => () => Cycle(-1),
                    _ => null
                };
                if (action != null) { Run(action); return true; } // swallow the chord
            }
        }
        return false;
    }

    /// <summary>Run an action on the UI thread (the hook callback runs on the same thread, but
    /// we post it so the hook returns immediately and Windows never drops it).</summary>
    private void Run(Action action)
    {
        try { _sync.BeginInvoke(action); } catch { }
    }

    // ---- Menu ----

    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add($"Lock current layout…  ({_lockKeyLabel})", null, (_, _) => LockCurrent());
        menu.Items.Add(new ToolStripSeparator());

        if (_state.Layouts.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("(no locked layouts yet)") { Enabled = false });
        }
        else
        {
            for (int i = 0; i < _state.Layouts.Count; i++)
            {
                int idx = i;
                var item = new ToolStripMenuItem(_state.Layouts[i].Name, null, (_, _) => Activate(idx))
                {
                    Checked = idx == _currentIndex
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());

            var manage = new ToolStripMenuItem("Manage layouts");
            for (int i = 0; i < _state.Layouts.Count; i++)
            {
                int idx = i;
                var sub = new ToolStripMenuItem(_state.Layouts[i].Name);
                sub.DropDownItems.Add("Rename…", null, (_, _) => Rename(idx));
                sub.DropDownItems.Add("Update to current windows", null, (_, _) => UpdateLayout(idx));
                sub.DropDownItems.Add("Delete", null, (_, _) => Delete(idx));
                manage.DropDownItems.Add(sub);
            }
            menu.Items.Add(manage);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Flip layouts:  double-tap Ctrl") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"   or  next {_nextKeyLabel}  ·  prev {_prevKeyLabel}") { Enabled = false });
        menu.Items.Add("How it works", null, (_, _) => ShowHelp());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _menu = menu;
        _tray.ContextMenuStrip = menu;
    }

    // ---- Actions ----

    private void LockCurrent()
    {
        var name = NameForm.Ask("Lock layout", "Name this layout (e.g. \"Coding\", \"Email\"):");
        if (name == null) return;

        // Re-locking onto an existing name = replace/update it.
        int existing = _state.Layouts.FindIndex(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var layout = CaptureCurrent(name);

        if (existing >= 0) { _state.Layouts[existing] = layout; _currentIndex = existing; }
        else { _state.Layouts.Add(layout); _currentIndex = _state.Layouts.Count - 1; }

        _state.Save();
        RebuildMenu();
        Notify("Layout locked", $"\"{name}\" — {layout.Windows.Count} window(s).");
    }

    private void UpdateLayout(int idx)
    {
        string name = _state.Layouts[idx].Name;
        _state.Layouts[idx] = CaptureCurrent(name);
        _currentIndex = idx;
        _state.Save();
        RebuildMenu();
        Notify("Layout updated", $"\"{name}\" now matches your current windows.");
    }

    private static LockedLayout CaptureCurrent(string name)
    {
        var layout = new LockedLayout { Name = name };
        foreach (var w in WindowManager.GetAltTabWindows())
        {
            layout.Windows.Add(new SavedWindow
            {
                Title = w.Title,
                Process = w.Process,
                Bounds = w.Bounds,
                Hwnd = w.Hwnd
            });
        }
        return layout;
    }

    private void Activate(int idx)
    {
        if (idx < 0 || idx >= _state.Layouts.Count) return;
        var layout = _state.Layouts[idx];

        var live = WindowManager.GetAltTabWindows();
        var used = new HashSet<IntPtr>();
        IntPtr primary = IntPtr.Zero;
        int restored = 0;

        foreach (var saved in layout.Windows)
        {
            IntPtr hwnd = Resolve(saved, live, used);
            if (hwnd == IntPtr.Zero) continue;
            used.Add(hwnd);

            WindowManager.MoveTo(hwnd, saved.Bounds);
            WindowManager.RaiseToTop(hwnd);
            saved.Hwnd = hwnd;              // refresh handle for this session
            if (primary == IntPtr.Zero) primary = hwnd;
            restored++;
        }

        if (primary != IntPtr.Zero) WindowManager.Focus(primary);

        _currentIndex = idx;
        RebuildMenu();
        Notify($"Switched to \"{layout.Name}\"",
            restored == layout.Windows.Count ? $"{restored} window(s)." : $"{restored} of {layout.Windows.Count} window(s) found.");
    }

    /// <summary>Find the live window that matches a saved one: live handle first, then process+title.</summary>
    private static IntPtr Resolve(SavedWindow saved, List<LiveWindow> live, HashSet<IntPtr> used)
    {
        if (WindowManager.IsAlive(saved.Hwnd) && !used.Contains(saved.Hwnd))
            return saved.Hwnd;

        // Exact title + process.
        foreach (var w in live)
            if (!used.Contains(w.Hwnd) && w.Process == saved.Process && w.Title == saved.Title)
                return w.Hwnd;

        // Same process, title starts the same (handles "file.txt - Notepad" drift).
        foreach (var w in live)
            if (!used.Contains(w.Hwnd) && w.Process == saved.Process &&
                (w.Title.StartsWith(saved.Title, StringComparison.OrdinalIgnoreCase) ||
                 saved.Title.StartsWith(w.Title, StringComparison.OrdinalIgnoreCase)))
                return w.Hwnd;

        // Last resort: any window of the same process.
        foreach (var w in live)
            if (!used.Contains(w.Hwnd) && w.Process == saved.Process)
                return w.Hwnd;

        return IntPtr.Zero;
    }

    private void Cycle(int direction)
    {
        if (_state.Layouts.Count == 0)
        {
            Notify("No locked layouts yet", $"Arrange your windows, then press {_lockKeyLabel} to lock one.");
            return;
        }
        int start = _currentIndex < 0 ? (direction > 0 ? -1 : 0) : _currentIndex;
        int next = ((start + direction) % _state.Layouts.Count + _state.Layouts.Count) % _state.Layouts.Count;
        Activate(next);
    }

    private void Rename(int idx)
    {
        var name = NameForm.Ask("Rename layout", "New name:", _state.Layouts[idx].Name);
        if (name == null) return;
        _state.Layouts[idx].Name = name;
        _state.Save();
        RebuildMenu();
    }

    private void Delete(int idx)
    {
        _state.Layouts.RemoveAt(idx);
        if (_currentIndex >= _state.Layouts.Count) _currentIndex = -1;
        _state.Save();
        RebuildMenu();
    }

    private void ShowHelp()
    {
        MessageBox.Show(
            "Fancy Schmancy Zones works alongside PowerToys FancyZones.\n\n" +
            "1. Arrange your windows (Shift+drag into zones with FancyZones).\n" +
            $"2. Press {_lockKeyLabel} to LOCK that arrangement and give it a name.\n" +
            "3. Build as many locked layouts as you like.\n\n" +
            "FLIP BETWEEN LAYOUTS:\n" +
            "   • Double-tap the Ctrl key  →  next layout (easiest)\n" +
            $"   • {_nextKeyLabel}  →  next locked layout\n" +
            $"   • {_prevKeyLabel}  →  previous locked layout\n\n" +
            "Flipping brings that layout's windows to their saved spots and to the front.\n" +
            "Click the tray icon (left or right) to pick, rename, update, or delete layouts.",
            "How it works — Fancy Schmancy Zones",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Use the app's own icon for the tray; fall back to a system icon.</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var ico = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (ico != null) return ico;
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void Notify(string title, string text)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(2500);
    }

    private void Quit()
    {
        _hook.Dispose();
        _sync.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    // Virtual-key codes we watch for.
    private const uint VK_Q = 0x51, VK_L = 0x4C, VK_W = 0x57;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12; // MENU = Alt
    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
}
