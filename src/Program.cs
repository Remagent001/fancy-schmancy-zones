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

        _settle.Tick += (_, _) => OnSettle();   // fires once cycling pauses; see Cycle()

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

        // While the card picker is open, its keys (Esc, 1–9) arrive here — the overlay is a
        // background window and usually can't hold keyboard focus, but this global hook sees keys
        // regardless. Consume them so they don't leak to whatever app is underneath.
        if (LayoutPickerForm.WantsKey(vk))
        {
            if (down) LayoutPickerForm.PressKey(vk);
            return true;
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
                        Run(OnDoubleTapCtrl);   // double-tap! what this does depends on the setting
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
        menu.ShowItemToolTips = true;

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
                // A ToolStripMenuItem's Click fires for the RIGHT button too, so wiring Activate to
                // Click made a right-click BOTH flip AND update — the flip won and the update never
                // stuck. Fix WITHOUT disturbing the proven left-click path: left-click still flips
                // via Click exactly as before; a right-click sets a one-shot flag so that same Click
                // skips the flip, and MouseUp does the update instead. The flag is set fresh on every
                // press, so it can't get stuck.
                bool rightClick = false;
                var item = new ToolStripMenuItem(_state.Layouts[i].Name, null, (_, _) =>
                {
                    if (rightClick) { rightClick = false; return; }   // right-click: don't flip
                    // If a flip / workspace-open is still mid-flight, Activate is a no-op; say so on
                    // screen instead of leaving the click looking dead.
                    if (!Activate(idx))
                        OsdForm.Flash(_state.Layouts[idx].Name, "Busy — finishing the last switch…");
                })
                {
                    Checked = idx == _currentIndex,
                    ToolTipText = "Left-click: switch to this layout  ·  Right-click: update it to your current windows"
                };
                item.MouseDown += (_, e) => rightClick = e.Button == MouseButtons.Right;
                item.MouseUp += (_, e) =>
                {
                    if (e.Button != MouseButtons.Right) return;
                    LogFlip($"right-click: update \"{_state.Layouts[idx].Name}\" to current windows");
                    // UpdateLayout rebuilds (disposes) this menu, so run it after the click unwinds.
                    _sync.BeginInvoke(new Action(() => UpdateLayout(idx)));
                };
                // Keyboard Enter fires Click with no preceding MouseDown, so it can't refresh
                // rightClick. Clear it every time the menu opens so an aborted right-press (released
                // off the menu, which fires no MouseUp/Click) can never carry a stale 'true' into a
                // later keyboard flip. Belt-and-suspenders with the per-press MouseDown reset.
                menu.Opening += (_, _) => rightClick = false;
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());

            // The deliberate "set up my whole workspace" button (e.g. after a reboot): opens the
            // layout's apps that aren't running, then arranges. Kept SEPARATE from the flip above,
            // which only ever arranges what's already open. Same layout names, two distinct jobs.
            var openWorkspace = new ToolStripMenuItem("Open a full workspace");
            for (int i = 0; i < _state.Layouts.Count; i++)
            {
                int idx = i;
                openWorkspace.DropDownItems.Add(_state.Layouts[i].Name, null, (_, _) => OpenAppsAndArrange(idx));
            }
            menu.Items.Add(openWorkspace);

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
        string modeHint = _state.Settings.DoubleTapCtrl switch
        {
            FlipMode.MostRecent => "double-tap Ctrl → most recent layout",
            FlipMode.PickCards  => "double-tap Ctrl → pick from cards",
            _                   => "double-tap Ctrl → next in order",
        };
        menu.Items.Add(new ToolStripMenuItem($"Flip layouts:  {modeHint}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"   or  next {_nextKeyLabel}  ·  prev {_prevKeyLabel}") { Enabled = false });

        var settings = new ToolStripMenuItem("Settings");

        // Triple toggle: what a double-tap of Ctrl does. Radio-style — one checked at a time.
        var flipMode = new ToolStripMenuItem("When I double-tap Ctrl…");
        void AddFlipMode(string label, FlipMode mode)
        {
            var mi = new ToolStripMenuItem(label) { Checked = _state.Settings.DoubleTapCtrl == mode };
            mi.Click += (_, _) => SetFlipMode(mode);
            flipMode.DropDownItems.Add(mi);
        }
        AddFlipMode("Cycle through layouts in order", FlipMode.InOrder);
        AddFlipMode("Switch to the most recent layout", FlipMode.MostRecent);
        AddFlipMode("Show all layouts to pick from", FlipMode.PickCards);
        settings.DropDownItems.Add(flipMode);
        settings.DropDownItems.Add(new ToolStripSeparator());

        var matchProfilesItem = new ToolStripMenuItem("Match Chrome/Edge browser profiles")
        {
            Checked = _state.Settings.MatchBrowserProfiles
        };
        matchProfilesItem.Click += (_, _) => ToggleMatchBrowserProfiles();
        settings.DropDownItems.Add(matchProfilesItem);

        var looseBrowserItem = new ToolStripMenuItem("Match browser windows even if the page changed")
        {
            Checked = _state.Settings.LooseBrowserMatch
        };
        looseBrowserItem.Click += (_, _) => ToggleLooseBrowserMatch();
        settings.DropDownItems.Add(looseBrowserItem);

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

        // So Keith (and anyone) can always see exactly which build is running — no guessing.
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"Fancy Schmancy Zones  v{AppVersion}") { Enabled = false });

        _menu = menu;
        _tray.ContextMenuStrip = menu;
    }

    /// <summary>The running build's version (e.g. "0.9.2"), read from the assembly so the menu
    /// can never disagree with what's actually installed.</summary>
    private static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // ---- Actions ----

    private void SetFlipMode(FlipMode mode)
    {
        _state.Settings.DoubleTapCtrl = mode;
        _state.Save();
        RebuildMenu();
        string what = mode switch
        {
            FlipMode.InOrder    => "cycles through your layouts in order",
            FlipMode.MostRecent => "jumps to your most recent layout",
            FlipMode.PickCards  => "shows all layouts to pick from",
            _                   => "",
        };
        OsdForm.Flash("Double-tap Ctrl", what);
    }

    private void ToggleLooseBrowserMatch()
    {
        _state.Settings.LooseBrowserMatch = !_state.Settings.LooseBrowserMatch;
        _state.Save();
        RebuildMenu();
        OsdForm.Flash("Browser matching",
            _state.Settings.LooseBrowserMatch
                ? "Placing browser windows by profile, even if the page changed"
                : "Browser windows only match their exact saved page");
    }

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
        // Confirm ON SCREEN, not via a Windows notification (those don't reliably show on Keith's
        // PC, so an update looked like it did nothing). Showing the count also lets him see exactly
        // how many windows got captured.
        int n = _state.Layouts[idx].Windows.Count;
        OsdForm.Flash(name, $"Updated ✓  —  saved your {n} open window{(n == 1 ? "" : "s")}");
        if (profileWarning != null) Notify("Layout updated", profileWarning);
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
        // Only save what you can actually SEE — skip windows completely buried behind others (a
        // window peeking out even a little is kept). Keeps incidental windows sitting hidden behind
        // the arrangement from being scooped into the layout. GetAltTabWindows is front-first.
        live = WindowManager.VisibleOnly(live);

        // Always note each browser window's profile at capture time — the saved profile is what the
        // loose same-profile browser fallback keys on later, and it's harmless metadata otherwise.
        WindowManager.FillProfiles(live);
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

    private bool Activate(int idx)
    {
        if (idx < 0 || idx >= _state.Layouts.Count) return true;   // nothing to do — don't retry
        if (_activating) return false;    // a flip is mid-flight; caller may retry shortly
        _settle.Stop();                   // a direct switch supersedes any pending cycle settle
        _activating = true;
        MarkUsed(idx);                    // for the "most recent" double-tap mode

        var layout = _state.Layouts[idx];
        _currentIndex = idx;
        RebuildMenu();
        OsdForm.Flash(layout.Name);

        // Do all the window shuffling OFF the UI thread. Even though the calls are now
        // non-blocking, keeping them off the UI thread guarantees the app and keyboard
        // input stay responsive no matter what other apps (e.g. Outlook) are doing.
        System.Threading.Tasks.Task.Run(() =>
        {
            int restored = 0;
            try { restored = ShuffleToLayout(layout, _state.Settings.MatchBrowserProfiles, _state.Settings.MinimizeOtherWindows, _state.Settings.LooseBrowserMatch); }
            catch (Exception ex) { Program.LogCrash(ex); }
            finally { _activating = false; }

            // NEVER use Windows notifications for flip status — they queue and dribble out one
            // every few seconds, so ten flips meant a minute of pop-ups. If some of the layout's
            // windows couldn't be found (closed since it was locked), say so in the on-screen
            // flash itself: instant, and gone in a couple of seconds.
            if (restored != layout.Windows.Count)
            {
                try
                {
                    _sync.BeginInvoke((Action)(() => OsdForm.Flash(layout.Name,
                        $"{restored} of {layout.Windows.Count} windows — the rest aren't open")));
                }
                catch { }
            }
        });
        return true;
    }

    /// <summary>Move/raise each of the layout's windows. Runs on a background thread.</summary>
    private static int ShuffleToLayout(LockedLayout layout, bool matchProfiles, bool minimizeOthers, bool looseBrowser)
    {
        var live = WindowManager.GetAltTabWindows();
        // Profiles are needed both for profile-aware placement AND for the loose same-profile
        // browser fallback, so detect them if either is on.
        if (matchProfiles || looseBrowser) WindowManager.FillProfiles(live);
        LogFlip($"flip to \"{layout.Name}\" ({layout.Windows.Count} saved, {live.Count} live, minimizeOthers={minimizeOthers})");
        // Pass 1: figure out which live window plays each saved role. No moving yet.
        var placements = MatchAll(layout, live, matchProfiles, looseBrowser);
        var used = new HashSet<IntPtr>(placements.Select(p => p.Hwnd));

        // Pass 2: minimize everything that's NOT part of this layout — leftovers from another
        // layout, or windows opened since. Done BEFORE raising the layout's windows so that
        // even a window that refuses to minimize ends up underneath, never on top.
        if (minimizeOthers)
        {
            var stubborn = WindowManager.MinimizeAll(
                live.Where(w => !used.Contains(w.Hwnd)).Select(w => w.Hwnd));
            foreach (var h in stubborn)
            {
                var w = live.FirstOrDefault(x => x.Hwnd == h);
                LogFlip($"  refused to minimize (pushed to back): {w?.Process} \"{w?.Title}\"");
            }
        }

        // Pass 3: move every window into place, then raise them BACK-TO-FRONT so the window that was
        // frontmost when you locked the layout ends up on top — restoring the stacking you saved.
        // placements are in front-to-back order (that's how they were captured), so raising the LAST
        // one first and the FIRST one last leaves the front window on top. Doing it this way (instead
        // of raising front-to-back and then relying on SetForegroundWindow to fight the order back)
        // makes the result identical on every flip, not "a different window on top each time."
        int restored = 0;
        foreach (var (hwnd, saved) in placements)
        {
            WindowManager.MoveTo(hwnd, saved.Bounds);
            saved.Hwnd = hwnd;              // refresh handle for this session
            restored++;
        }
        for (int i = placements.Count - 1; i >= 0; i--)
            WindowManager.RaiseToTop(placements[i].Hwnd);

        IntPtr primary = placements.Count > 0 ? placements[0].Hwnd : IntPtr.Zero;
        if (primary != IntPtr.Zero) WindowManager.Focus(primary);
        return restored;
    }

    /// <summary>Append a line to flip.log so "a window stayed on top" reports can be diagnosed
    /// from facts instead of guesses. Self-truncates so it can never grow unbounded.</summary>
    internal static void LogFlip(string message)
    {
        try
        {
            string path = Path.Combine(AppState.Dir, "flip.log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    /// <summary>
    /// Launch any of the layout's apps that aren't currently open, wait for their
    /// windows to appear, then arrange everything into the saved layout.
    /// </summary>
    private void OpenAppsAndArrange(int idx)
    {
        if (idx < 0 || idx >= _state.Layouts.Count) return;
        if (_activating) { OsdForm.Flash(_state.Layouts[idx].Name, "Busy — one moment…"); return; }
        _activating = true;
        MarkUsed(idx);                    // for the "most recent" double-tap mode

        var layout = _state.Layouts[idx];
        _currentIndex = idx;
        RebuildMenu();
        OsdForm.Flash(layout.Name, "Opening apps…");   // visible feedback (toasts don't show on Keith's PC)

        bool matchProfiles = _state.Settings.MatchBrowserProfiles;
        bool minimizeOthers = _state.Settings.MinimizeOtherWindows;
        bool launchTerminals = _state.Settings.LaunchTerminalApps;
        bool looseBrowser = _state.Settings.LooseBrowserMatch;

        System.Threading.Tasks.Task.Run(() =>
        {
            int launched = 0, skippedTerminals = 0;
            try
            {
                var live = WindowManager.GetAltTabWindows();
                if (matchProfiles || looseBrowser) WindowManager.FillProfiles(live);

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

                ShuffleToLayout(layout, matchProfiles, minimizeOthers, looseBrowser);
            }
            catch (Exception ex) { Program.LogCrash(ex); }
            finally { _activating = false; }

            try
            {
                int opened = launched, skipped = skippedTerminals;
                // Visible on-screen result (not a Windows toast, which doesn't show on Keith's PC).
                string sub = opened > 0
                    ? $"Opened ✓  —  launched {opened} app{(opened == 1 ? "" : "s")}, arranged"
                    : "Arranged ✓  —  apps were already open";
                if (skipped > 0)
                    sub += $"  ·  {skipped} terminal{(skipped == 1 ? "" : "s")} skipped (Settings can turn these on)";
                _sync.BeginInvoke((Action)(() => OsdForm.Flash(layout.Name, sub)));
            }
            catch { }
        });
    }

    /// <summary>
    /// Decide which live window plays each saved role — strongest evidence first, across the
    /// WHOLE layout. Every tier runs for all saved windows before the next, looser tier gets a
    /// turn, so a saved window whose real window is gone can never steal another saved window's
    /// exact match and set off a chain of wrong placements. There is deliberately NO "any window
    /// of the same app" fallback: in practice it only ever grabbed the wrong window (a different
    /// browser page, a different message) and raised it on top. A saved window that isn't open is
    /// simply left out — the app never substitutes a look-alike.
    /// </summary>
    private static List<(IntPtr Hwnd, SavedWindow Saved)> MatchAll(LockedLayout layout, List<LiveWindow> live, bool matchProfiles, bool looseBrowser)
    {
        var saved = layout.Windows;
        var match = new IntPtr[saved.Count];
        var used = new HashSet<IntPtr>();

        // For title-evidence tiers: only a KNOWN different browser profile blocks a match.
        bool ProfileOk(SavedWindow s, LiveWindow w) =>
            !matchProfiles || !WindowManager.IsChromium(s.Process) ||
            string.IsNullOrEmpty(s.Profile) || string.IsNullOrEmpty(w.Profile) ||
            string.Equals(s.Profile, w.Profile, StringComparison.OrdinalIgnoreCase);

        var tiers = new (string How, Func<SavedWindow, LiveWindow, bool> Fits)[]
        {
            // The very same window as earlier this session — survives any title change.
            ("handle", (s, w) => s.Hwnd != IntPtr.Zero && w.Hwnd == s.Hwnd &&
                w.Process == s.Process && ProfileOk(s, w)),
            ("exact title", (s, w) => w.Process == s.Process && w.Title == s.Title && ProfileOk(s, w)),
            ("title start", (s, w) => w.Process == s.Process && ProfileOk(s, w) &&
                (w.Title.StartsWith(s.Title, StringComparison.OrdinalIgnoreCase) ||
                 s.Title.StartsWith(w.Title, StringComparison.OrdinalIgnoreCase))),
            ("title contains", (s, w) => w.Process == s.Process && ProfileOk(s, w) &&
                Math.Min(s.Title.Length, w.Title.Length) >= 5 &&
                (w.Title.Contains(s.Title, StringComparison.OrdinalIgnoreCase) ||
                 s.Title.Contains(w.Title, StringComparison.OrdinalIgnoreCase))),
            // Loose browser fallback (opt-out via Settings): a Chrome/Edge window of the SAME PROFILE,
            // regardless of which tab/page it's showing now. Runs LAST, so a window still on its saved
            // page always claims its own slot first; only leftover same-profile browser windows fill
            // leftover browser slots. This is what places your browser windows when you've switched
            // tabs since locking (their title changed) — same-profile windows are interchangeable and
            // can't be told apart any other way. Never crosses profiles, never grabs a minimized
            // window, and is count-bounded (only as many windows as there are slots). This deliberately
            // does NOT apply to any non-browser app (a generic "same app" grab was wrong every time).
            ("same-profile browser", (s, w) => looseBrowser &&
                w.Process == s.Process && WindowManager.IsChromium(s.Process) &&
                !WindowManager.IsMinimized(w.Hwnd) &&
                !string.IsNullOrEmpty(s.Profile) && !string.IsNullOrEmpty(w.Profile) &&
                string.Equals(s.Profile, w.Profile, StringComparison.OrdinalIgnoreCase)),
        };

        foreach (var (how, fits) in tiers)
            for (int i = 0; i < saved.Count; i++)
            {
                if (match[i] != IntPtr.Zero) continue;
                var w = live.FirstOrDefault(x => !used.Contains(x.Hwnd) && fits(saved[i], x));
                if (w == null) continue;
                match[i] = w.Hwnd;
                used.Add(w.Hwnd);
                LogFlip($"  {saved[i].Process} \"{saved[i].Title}\" -> \"{w.Title}\" ({how})");
            }

        var result = new List<(IntPtr, SavedWindow)>();
        for (int i = 0; i < saved.Count; i++)
            if (match[i] != IntPtr.Zero) result.Add((match[i], saved[i]));
            else LogFlip($"  no match for: {saved[i].Process} \"{saved[i].Title}\" (not open — left out)");
        return result;
    }

    // Cycling is debounced like Alt+Tab: each double-tap just advances the selection and
    // flashes the layout's NAME on screen instantly. The actual window shuffling (the slow
    // part) only happens once, ~2/3 of a second after the last tap — so skimming past five
    // layouts costs nothing, and there's no pile-up of window moves or notifications.
    private readonly System.Windows.Forms.Timer _settle = new() { Interval = 650 };

    // Layout names in most-recently-used order (index 0 = current). Drives the "most recent"
    // double-tap mode so you can bounce between the two layouts you actually use. Names (not
    // indexes) so it survives reordering; entries for deleted layouts are simply ignored.
    private readonly List<string> _recentOrder = new();

    private void MarkUsed(int idx)
    {
        if (idx < 0 || idx >= _state.Layouts.Count) return;
        string name = _state.Layouts[idx].Name;
        _recentOrder.Remove(name);
        _recentOrder.Insert(0, name);
    }

    /// <summary>The most-recently-used layout that ISN'T the current one — the "swap back" target.
    /// Falls back to the next one in order if there's no usable history yet.</summary>
    private int MostRecentOtherLayout()
    {
        foreach (var name in _recentOrder)
        {
            int i = _state.Layouts.FindIndex(l => l.Name == name);
            if (i >= 0 && i != _currentIndex) return i;
        }
        if (_state.Layouts.Count == 0) return -1;
        int start = _currentIndex < 0 ? 0 : _currentIndex;
        return (start + 1) % _state.Layouts.Count;
    }

    /// <summary>Double-tap Ctrl. What it does depends on the user's chosen flip mode.</summary>
    private void OnDoubleTapCtrl()
    {
        switch (_state.Settings.DoubleTapCtrl)
        {
            case FlipMode.MostRecent: FlipToMostRecent(); break;
            case FlipMode.PickCards:  ShowLayoutPicker();  break;
            default:                  Cycle(+1);           break;   // InOrder
        }
    }

    private void FlipToMostRecent()
    {
        if (_state.Layouts.Count == 0)
        {
            Notify("No locked layouts yet", $"Arrange your windows, then press {_lockKeyLabel} to lock one.");
            return;
        }
        int target = MostRecentOtherLayout();
        if (target >= 0) RequestFlip(target);
    }

    private void ShowLayoutPicker()
    {
        if (_state.Layouts.Count == 0)
        {
            Notify("No locked layouts yet", $"Arrange your windows, then press {_lockKeyLabel} to lock one.");
            return;
        }
        _settle.Stop();   // cancel any pending cycle so nothing flips windows behind the picker

        bool matchProfiles = _state.Settings.MatchBrowserProfiles;
        bool looseBrowser = _state.Settings.LooseBrowserMatch;
        var layouts = _state.Layouts;
        int currentIndex = _currentIndex;

        // Work out each card's OPEN windows OFF the UI thread — FillProfiles walks Chrome's
        // accessibility tree per browser window and can take a moment; on the UI thread that would
        // freeze the tray/OSD before the picker even appears. Then show the picker on the UI thread.
        // (Each card previews only the windows a flip would actually place.)
        System.Threading.Tasks.Task.Run(() =>
        {
            List<LayoutPickerForm.CardInfo> cards;
            try
            {
                var live = WindowManager.GetAltTabWindows();
                if (matchProfiles || looseBrowser) WindowManager.FillProfiles(live);
                cards = layouts.Select(layout => new LayoutPickerForm.CardInfo(
                    layout.Name,
                    MatchAll(layout, live, matchProfiles, looseBrowser).Select(p => p.Saved.Bounds).ToList())).ToList();
            }
            catch (Exception ex) { Program.LogCrash(ex); return; }

            // Apply immediately if we can; if a previous switch is still finishing (_activating),
            // queue via the settle timer's retry so the pick is never silently dropped.
            _sync.BeginInvoke((Action)(() => LayoutPickerForm.Show(cards, currentIndex, idx =>
            {
                if (!Activate(idx)) RequestFlip(idx);
            })));
        });
    }

    private void Cycle(int direction)
    {
        if (LayoutPickerForm.IsOpen) return;   // don't flip windows behind an open card picker
        if (_state.Layouts.Count == 0)
        {
            Notify("No locked layouts yet", $"Arrange your windows, then press {_lockKeyLabel} to lock one.");
            return;
        }
        int start = _currentIndex < 0 ? (direction > 0 ? -1 : 0) : _currentIndex;
        RequestFlip(((start + direction) % _state.Layouts.Count + _state.Layouts.Count) % _state.Layouts.Count);
    }

    /// <summary>Select a layout and arrange it after the debounce settle — the shared path for
    /// cycling, most-recent, and a picker pick that had to wait for a busy flip. OnSettle retries
    /// Activate until the switch actually applies, so a request is never dropped.</summary>
    private void RequestFlip(int idx)
    {
        if (idx < 0 || idx >= _state.Layouts.Count) return;
        _currentIndex = idx;
        OsdForm.Flash(_state.Layouts[idx].Name);
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>The user stopped cycling — arrange the layout they landed on. If a previous
    /// flip is still mid-flight, the timer stays armed and simply tries again on its next tick.</summary>
    private void OnSettle()
    {
        if (LayoutPickerForm.IsOpen) { _settle.Stop(); return; }   // never rearrange behind the picker
        if (Activate(_currentIndex)) _settle.Stop();
    }

    private void Rename(int idx)
    {
        var oldName = _state.Layouts[idx].Name;
        var name = NameForm.Ask("Rename layout", "New name:", oldName);
        if (name == null) return;
        _state.Layouts[idx].Name = name;
        int ri = _recentOrder.IndexOf(oldName);   // keep most-recent history pointing at this layout
        if (ri >= 0) _recentOrder[ri] = name;
        _state.Save();
        RebuildMenu();
    }

    private void Delete(int idx)
    {
        _state.Layouts.RemoveAt(idx);
        // Keep _currentIndex pointing at the SAME layout (it shifts down if we removed one above it).
        if (idx == _currentIndex) _currentIndex = -1;
        else if (idx < _currentIndex) _currentIndex--;
        if (_currentIndex >= _state.Layouts.Count) _currentIndex = -1;   // backstop
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
            "Flip quickly several times to skim — the layout name pops up on screen as you go, " +
            "and the windows arrange once you stop.\n" +
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
        _settle.Stop();
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
