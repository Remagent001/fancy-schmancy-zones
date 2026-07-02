using System.IO;
using System.Linq;
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

        // Don't let a stray error kill the app — log it and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }

    internal static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppState.Dir);
            File.AppendAllText(Path.Combine(AppState.Dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
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
                sub.DropDownItems.Add("Open apps + arrange", null, (_, _) => OpenAppsAndArrange(idx));
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

        var settings = new ToolStripMenuItem("Settings");
        var matchProfilesItem = new ToolStripMenuItem("Match Chrome/Edge browser profiles")
        {
            Checked = _state.Settings.MatchBrowserProfiles
        };
        matchProfilesItem.Click += (_, _) => ToggleMatchBrowserProfiles();
        settings.DropDownItems.Add(matchProfilesItem);

        var minimizeOthersItem = new ToolStripMenuItem("Minimize other windows when switching layouts")
        {
            Checked = _state.Settings.MinimizeOtherWindows
        };
        minimizeOthersItem.Click += (_, _) => ToggleMinimizeOtherWindows();
        settings.DropDownItems.Add(minimizeOthersItem);

        var launchTerminalsItem = new ToolStripMenuItem("Launch terminal/console windows when opening a layout")
        {
            Checked = _state.Settings.LaunchTerminalApps
        };
        launchTerminalsItem.Click += (_, _) => ToggleLaunchTerminalApps();
        settings.DropDownItems.Add(launchTerminalsItem);

        menu.Items.Add(settings);
        menu.Items.Add("Rescue lost windows", null, (_, _) => RescueWindows());

        menu.Items.Add("How it works", null, (_, _) => ShowHelp());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _menu = menu;
        _tray.ContextMenuStrip = menu;
    }

    // ---- Actions ----

    private void ToggleMatchBrowserProfiles()
    {
        _state.Settings.MatchBrowserProfiles = !_state.Settings.MatchBrowserProfiles;
        _state.Save();
        RebuildMenu();
        Notify("Setting changed",
            _state.Settings.MatchBrowserProfiles
                ? "Layouts will remember which Chrome/Edge profile each window used."
                : "Browser profiles will be ignored — all Chrome/Edge windows are treated the same.");
    }

    private void ToggleMinimizeOtherWindows()
    {
        _state.Settings.MinimizeOtherWindows = !_state.Settings.MinimizeOtherWindows;
        _state.Save();
        RebuildMenu();
        Notify("Setting changed",
            _state.Settings.MinimizeOtherWindows
                ? "Switching layouts will minimize anything that isn't part of the layout."
                : "Switching layouts will leave other windows alone.");
    }

    private void ToggleLaunchTerminalApps()
    {
        _state.Settings.LaunchTerminalApps = !_state.Settings.LaunchTerminalApps;
        _state.Save();
        RebuildMenu();
        Notify("Setting changed",
            _state.Settings.LaunchTerminalApps
                ? "\"Open apps + arrange\" will launch blank terminal windows for missing ones."
                : "\"Open apps + arrange\" will skip terminal windows — open those yourself, then flip.");
    }

    /// <summary>Bring any window whose saved position is off every currently connected
    /// monitor back onto the primary screen — e.g. one left minimized after a monitor/dock
    /// change, that the taskbar can activate but never actually shows.</summary>
    private void RescueWindows()
    {
        int fixedCount = 0;
        foreach (var w in WindowManager.GetAltTabWindows())
            if (WindowManager.EnsureOnScreen(w.Hwnd)) fixedCount++;

        Notify("Rescue lost windows",
            fixedCount > 0
                ? $"Brought {fixedCount} window(s) back onto your screen."
                : "Nothing to fix — all your windows are already on-screen.");
    }

    private void LockCurrent()
    {
        var name = NameForm.Ask("Lock layout", "Name this layout (e.g. \"Coding\", \"Email\"):");
        if (name == null) return;

        // Re-locking onto an existing name = replace/update it.
        int existing = _state.Layouts.FindIndex(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var layout = CaptureCurrent(name, _state.Settings.MatchBrowserProfiles, out string? profileWarning);

        if (existing >= 0) { _state.Layouts[existing] = layout; _currentIndex = existing; }
        else { _state.Layouts.Add(layout); _currentIndex = _state.Layouts.Count - 1; }

        _state.Save();
        RebuildMenu();
        Notify("Layout locked", profileWarning ?? $"\"{name}\" — {layout.Windows.Count} window(s).");
    }

    private void UpdateLayout(int idx)
    {
        string name = _state.Layouts[idx].Name;
        _state.Layouts[idx] = CaptureCurrent(name, _state.Settings.MatchBrowserProfiles, out string? profileWarning);
        _currentIndex = idx;
        _state.Save();
        RebuildMenu();
        Notify("Layout updated", profileWarning ?? $"\"{name}\" now matches your current windows.");
    }

    /// <summary>
    /// If any captured Chrome/Edge window's profile couldn't be pinned down — most often
    /// because two of that browser's profiles share the same display name — say so, rather
    /// than silently guessing which one it was.
    /// </summary>
    private static string? BuildProfileWarning(List<LiveWindow> live)
    {
        var unresolvedBrowsers = live
            .Where(w => WindowManager.IsChromium(w.Process) && string.IsNullOrEmpty(w.Profile))
            .Select(w => w.Process).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(BrowserProfiles.HasAmbiguousProfiles)
            .ToList();
        if (unresolvedBrowsers.Count == 0) return null;

        return "Heads up: some of your Chrome/Edge profiles share the same name, so those windows " +
               "couldn't be matched to a specific one — give them distinct names in the browser to fix this. " +
               "(Or turn off profile matching in Settings.)";
    }

    private static LockedLayout CaptureCurrent(string name, bool matchProfiles, out string? profileWarning)
    {
        var layout = new LockedLayout { Name = name };

        // A layout is "what's arranged on screen right now" — so minimized windows are left out.
        // (Their reported position is also garbage: Windows parks minimized windows tens of
        // thousands of pixels off-screen, and saving that teleports them into the void later.)
        // Same for anything already off every monitor.
        var live = WindowManager.GetAltTabWindows()
            .Where(w => !WindowManager.IsMinimized(w.Hwnd) && WindowManager.IsOnScreen(w.Bounds))
            .ToList();

        if (matchProfiles) WindowManager.FillProfiles(live);   // note which Chrome/Edge profile each browser window is
        profileWarning = matchProfiles ? BuildProfileWarning(live) : null;

        foreach (var w in live)
        {
            layout.Windows.Add(new SavedWindow
            {
                Title = w.Title,
                Process = w.Process,
                ExePath = w.ExePath,
                Profile = w.Profile,
                Bounds = w.Bounds,
                Hwnd = w.Hwnd
            });
        }
        return layout;
    }

    private volatile bool _activating;

    private void Activate(int idx)
    {
        if (idx < 0 || idx >= _state.Layouts.Count) return;
        if (_activating) return;          // ignore rapid repeat flips while one is in flight
        _activating = true;

        var layout = _state.Layouts[idx];
        _currentIndex = idx;
        RebuildMenu();

        // Do all the window shuffling OFF the UI thread. Even though the calls are now
        // non-blocking, keeping them off the UI thread guarantees the app and keyboard
        // input stay responsive no matter what other apps (e.g. Outlook) are doing.
        System.Threading.Tasks.Task.Run(() =>
        {
            int restored = 0;
            try { restored = ShuffleToLayout(layout, _state.Settings.MatchBrowserProfiles, _state.Settings.MinimizeOtherWindows); }
            catch (Exception ex) { Program.LogCrash(ex); }
            finally { _activating = false; }

            try
            {
                _sync.BeginInvoke((Action)(() => Notify($"Switched to \"{layout.Name}\"",
                    restored == layout.Windows.Count ? $"{restored} window(s)." : $"{restored} of {layout.Windows.Count} window(s) found.")));
            }
            catch { }
        });
    }

    /// <summary>Move/raise each of the layout's windows. Runs on a background thread.</summary>
    private static int ShuffleToLayout(LockedLayout layout, bool matchProfiles, bool minimizeOthers)
    {
        var live = WindowManager.GetAltTabWindows();
        if (matchProfiles) WindowManager.FillProfiles(live);   // so we place each Chrome window into its own profile's spot
        var used = new HashSet<IntPtr>();
        IntPtr primary = IntPtr.Zero;
        int restored = 0;

        foreach (var saved in layout.Windows)
        {
            IntPtr hwnd = Resolve(saved, live, used, matchProfiles);
            if (hwnd == IntPtr.Zero) continue;
            used.Add(hwnd);

            WindowManager.MoveTo(hwnd, saved.Bounds);
            WindowManager.RaiseToTop(hwnd);
            saved.Hwnd = hwnd;              // refresh handle for this session
            if (primary == IntPtr.Zero) primary = hwnd;
            restored++;
        }

        // Anything not part of THIS layout — leftovers from another layout, or windows opened
        // since — gets minimized, so the layout you switched to is never left partly hidden.
        // Retries a few times: a busy window (e.g. a terminal mid-output) can miss the first ask.
        if (minimizeOthers)
            WindowManager.MinimizeAll(live.Where(w => !used.Contains(w.Hwnd)).Select(w => w.Hwnd));

        if (primary != IntPtr.Zero) WindowManager.Focus(primary);
        return restored;
    }

    /// <summary>
    /// Launch any of the layout's apps that aren't currently open, wait for their
    /// windows to appear, then arrange everything into the saved layout.
    /// </summary>
    private void OpenAppsAndArrange(int idx)
    {
        if (idx < 0 || idx >= _state.Layouts.Count) return;
        if (_activating) return;
        _activating = true;

        var layout = _state.Layouts[idx];
        _currentIndex = idx;
        RebuildMenu();

        bool matchProfiles = _state.Settings.MatchBrowserProfiles;
        bool minimizeOthers = _state.Settings.MinimizeOtherWindows;
        bool launchTerminals = _state.Settings.LaunchTerminalApps;

        System.Threading.Tasks.Task.Run(() =>
        {
            int launched = 0, skippedTerminals = 0;
            try
            {
                var live = WindowManager.GetAltTabWindows();
                if (matchProfiles) WindowManager.FillProfiles(live);

                // Identify an open app by program + browser profile, so a Chrome profile that
                // isn't open yet still gets launched even though chrome.exe is already running.
                static string Key(string proc, string profile) => proc + "|" + profile;

                var runningKeys = new HashSet<string>(
                    live.Select(w => Key(w.Process, w.Profile)), StringComparer.OrdinalIgnoreCase);

                var launchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var saved in layout.Windows)
                {
                    if (string.IsNullOrEmpty(saved.ExePath)) continue;
                    string key = Key(saved.Process, matchProfiles ? saved.Profile : "");
                    if (runningKeys.Contains(key)) continue;          // that app/profile already open
                    if (!launchedKeys.Add(key)) continue;             // don't launch the same app/profile twice
                    if (!launchTerminals && WindowManager.IsTerminalHost(saved.Process))
                    {
                        skippedTerminals++;   // a blank terminal doesn't reproduce what was running in it
                        continue;
                    }
                    string profile = matchProfiles ? saved.Profile : "";
                    if (WindowManager.Launch(saved.ExePath, WindowManager.ProfileArgs(profile))) launched++;
                }

                // Give the freshly launched apps time to put their windows up, then arrange.
                // Poll so we don't wait longer than needed, but cap the total wait.
                if (launched > 0)
                {
                    int wanted = layout.Windows.Count;
                    for (int i = 0; i < 20; i++)   // up to ~10s
                    {
                        System.Threading.Thread.Sleep(500);
                        if (WindowManager.GetAltTabWindows().Count >= wanted) break;
                    }
                }

                ShuffleToLayout(layout, matchProfiles, minimizeOthers);
            }
            catch (Exception ex) { Program.LogCrash(ex); }
            finally { _activating = false; }

            try
            {
                int opened = launched, skipped = skippedTerminals;
                string msg = opened > 0 ? $"Launched {opened} app(s) and arranged the layout."
                                         : "All apps were already open — arranged the layout.";
                if (skipped > 0)
                    msg += $" Skipped {skipped} terminal window(s) — open those yourself, then flip to snap them into place.";
                _sync.BeginInvoke((Action)(() => Notify($"Opened \"{layout.Name}\"", msg)));
            }
            catch { }
        });
    }

    /// <summary>Find the live window that matches a saved one: live handle first, then process+title.</summary>
    private static IntPtr Resolve(SavedWindow saved, List<LiveWindow> live, HashSet<IntPtr> used, bool matchProfiles)
    {
        if (WindowManager.IsAlive(saved.Hwnd) && !used.Contains(saved.Hwnd))
            return saved.Hwnd;

        // Chrome/Edge with a known profile: only ever match the SAME profile, so a window
        // never lands in the wrong profile's spot. Prefer the same page title within it.
        if (matchProfiles && WindowManager.IsChromium(saved.Process) && !string.IsNullOrEmpty(saved.Profile))
        {
            IntPtr firstOfProfile = IntPtr.Zero;
            foreach (var w in live)
            {
                if (used.Contains(w.Hwnd) || w.Process != saved.Process ||
                    !string.Equals(w.Profile, saved.Profile, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (w.Title == saved.Title) return w.Hwnd;
                if (firstOfProfile == IntPtr.Zero) firstOfProfile = w.Hwnd;
            }
            return firstOfProfile;
        }

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
            "Click the tray icon (left or right) to pick, rename, update, or delete layouts.\n\n" +
            "REOPENING APPS:\n" +
            "\"Open apps + arrange\" (under Manage layouts) launches any apps in that layout that " +
            "aren't already running, then arranges everything — handy after a reboot.\n\n" +
            "CHROME/EDGE PROFILES:\n" +
            "Layouts remember which browser profile each window used and reopen that same one. " +
            "This is read from the little profile button in the browser's toolbar, so if two " +
            "profiles share the exact same name, those windows can't be told apart — give them " +
            "distinct names in the browser to fix it. You can turn this off entirely under " +
            "Settings → \"Match Chrome/Edge browser profiles.\"\n\n" +
            "KEEPING THINGS TIDY:\n" +
            "By default, switching to a layout minimizes anything NOT part of that layout — " +
            "leftovers from another layout, or windows you opened since — so what you switch to " +
            "is never left partly hidden. Turn this off under Settings → \"Minimize other windows " +
            "when switching layouts.\"",
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
