using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace FancySchmancyZones;

/// <summary>
/// Works out which Chrome/Edge/Brave profile a browser window is using.
///
/// Chromium doesn't expose the profile through the window or the process — Chrome's toolbar
/// avatar button (class "AvatarToolbarButton") is labelled with the profile's display name,
/// and Edge has its own version (class "EdgeAvatarToolbarButton", labelled e.g.
/// "Personal Profile" or "Profile 2 Profile, Please sign in"). We read that label via UI
/// Automation and map it to the on-disk profile folder (e.g. "Default", "Profile 10") that
/// --profile-directory needs to relaunch that exact profile.
///
/// IMPORTANT: if two profiles share the same display name, the label alone can't tell them
/// apart — nothing in the accessibility tree reveals more (checked: no unique AutomationId,
/// no tooltip, no hidden value). Rather than guess and risk landing a window in the wrong
/// profile, ambiguous names are treated as "unknown" so callers fall back to their normal,
/// profile-blind matching.
/// </summary>
public static class BrowserProfiles
{
    private static string? UserDataDir(string proc)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return proc.ToLowerInvariant() switch
        {
            "chrome" => Path.Combine(local, "Google", "Chrome", "User Data"),
            "msedge" => Path.Combine(local, "Microsoft", "Edge", "User Data"),
            "brave"  => Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
            _ => null
        };
    }

    private static string AvatarClassName(string proc) =>
        proc.Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "EdgeAvatarToolbarButton" : "AvatarToolbarButton";

    private sealed class ProfileMap
    {
        // Display label -> profile folder, for labels that identify exactly one profile.
        public readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);
        // Labels shared by 2+ profiles — deliberately excluded from Map above.
        public readonly HashSet<string> Ambiguous = new(StringComparer.OrdinalIgnoreCase);
        // The other direction: profile folder -> its display name ("Profile 5" -> "Work"), for
        // naming a profile we've already identified. Unlike Map, duplicates are no problem here.
        public readonly Dictionary<string, string> FolderNames = new(StringComparer.OrdinalIgnoreCase);
    }

    // Profile lists barely change during a session, so read the file once per browser.
    private static readonly Dictionary<string, ProfileMap> _cache = new();

    private static ProfileMap GetMap(string proc)
    {
        string key = proc.ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var result = new ProfileMap();
        try
        {
            var dir = UserDataDir(proc);
            var path = dir == null ? null : Path.Combine(dir, "Local State");
            if (path != null && File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("profile", out var pe) &&
                    pe.TryGetProperty("info_cache", out var ic))
                {
                    foreach (var prof in ic.EnumerateObject())
                    {
                        foreach (var label in DisplayLabels(prof.Value))
                            AddLabel(result, label, prof.Name);

                        // "name" is the profile's own name ("Work"), which is what Window Cascade
                        // showed in its menu — keep that, so the entries read the way Keith knows.
                        if (prof.Value.TryGetProperty("name", out var nm) &&
                            nm.GetString() is { Length: > 0 } friendly)
                            result.FolderNames[prof.Name] = friendly;
                    }
                }
            }
        }
        catch { /* unreadable Local State — profiles just won't be distinguished */ }

        _cache[key] = result;
        return result;
    }

    // A profile can show up under more than one label: its internal "name" (what the avatar
    // button on Chrome shows) and, on Edge, a separate "shortcut_name" (what Edge's button and
    // window title show, e.g. "Personal" for the primary profile).
    private static IEnumerable<string> DisplayLabels(JsonElement profile)
    {
        string? name = profile.TryGetProperty("name", out var n) ? n.GetString() : null;
        string? shortcut = profile.TryGetProperty("shortcut_name", out var s) ? s.GetString() : null;
        if (!string.IsNullOrEmpty(name)) yield return name;
        if (!string.IsNullOrEmpty(shortcut) && !string.Equals(shortcut, name, StringComparison.OrdinalIgnoreCase))
            yield return shortcut;
    }

    private static void AddLabel(ProfileMap m, string label, string folder)
    {
        if (m.Ambiguous.Contains(label)) return;
        if (m.Map.TryGetValue(label, out var existing))
        {
            if (!existing.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                m.Map.Remove(label);
                m.Ambiguous.Add(label);   // two different profiles, same label — can't tell apart
            }
        }
        else m.Map[label] = folder;
    }

    /// <summary>True if this browser has 2+ profiles sharing a display name (detection is unreliable for those).</summary>
    public static bool HasAmbiguousProfiles(string proc) => GetMap(proc).Ambiguous.Count > 0;

    /// <summary>
    /// The profile FOLDER this browser window is using (e.g. "Profile 5"), or "" if unknown
    /// (no avatar found, or its name is shared by more than one profile).
    ///
    /// KNOWN WRONG, and not yet fixed here on purpose (2026-07-15). Measured against the window's
    /// own AppUserModelID (see ProfileOf) across Keith's 18 open Chrome windows, this returned the
    /// WRONG profile for 6 of them — reporting Profile 2 windows as "Profile 5", because the last
    /// resort below matches any known profile name found anywhere in the avatar label. That means
    /// layouts can save a browser window under the wrong profile, and "Open apps + arrange" can
    /// reopen the wrong login.
    ///
    /// ProfileOf is exact and 85x faster, so this should be retired for it — but layouts.json
    /// already holds profile folders written by THIS detector, so swapping it in blind would make
    /// saved layouts stop matching the windows they were locked from. That needs a migration and
    /// Keith's hands-on check, so it's a session of its own, not a drive-by.
    /// </summary>
    public static string DetectFolder(IntPtr hwnd, string proc)
    {
        var map = GetMap(proc);
        if (map.Map.Count == 0 && map.Ambiguous.Count == 0) return "";
        try
        {
            var cond = new System.Windows.Automation.PropertyCondition(
                AutomationElement.ClassNameProperty, AvatarClassName(proc));
            var el = AutomationElement.FromHandle(hwnd);
            var avatar = el?.FindFirst(TreeScope.Descendants, cond);
            return MatchProfile(avatar?.Current.Name, map);
        }
        catch { return ""; }
    }

    private static string MatchProfile(string? raw, ProfileMap map)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = CleanLabel(raw);

        // Prefer a parenthetical profile name if present: "Keith (188PHV)" -> "188PHV".
        int lp = s.LastIndexOf('('), rp = s.LastIndexOf(')');
        if (lp >= 0 && rp > lp)
        {
            string inner = s[(lp + 1)..rp].Trim();
            if (map.Map.TryGetValue(inner, out var f1)) return f1;
            if (map.Ambiguous.Contains(inner)) return "";
        }

        if (map.Map.TryGetValue(s, out var f2)) return f2;
        if (map.Ambiguous.Contains(s)) return "";

        // Last resort: any known, unambiguous profile name appearing in the label.
        foreach (var kv in map.Map)
            if (s.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return "";
    }

    // Strips the chrome around the actual profile name in each browser's avatar label:
    // Chrome: "Hi, Keith" -> "Keith"
    // Edge:   "Personal Profile" -> "Personal" ; "Profile 2 Profile, Please sign in" -> "Profile 2"
    private static string CleanLabel(string raw)
    {
        string s = raw.Trim();
        s = Regex.Replace(s, @"^Hi,\s*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @",\s*Please sign in$", "", RegexOptions.IgnoreCase).Trim();
        s = Regex.Replace(s, @"\s+Profile$", "", RegexOptions.IgnoreCase).Trim();
        return s;
    }

    // ── Which profile is THIS window? (via AppUserModelID) ───────────────────────────────────
    //
    // Ported from Window Cascade, which got this right first. Chrome runs every profile inside one
    // process, so a window's profile can't be had from the pid or the command line — but Chrome
    // stamps each window with the per-profile AppUserModelID that Windows itself uses to group
    // taskbar icons ("Chrome.UserData.Profile5"). Reading that tag is exact, and needs no Chrome
    // setting turned on.
    //
    // It is also both faster and MORE ACCURATE than the avatar-label reading above: measured on
    // Keith's 18 open Chrome windows, this took 0.16ms each vs 13.6ms, and the label reader got 6
    // of them WRONG (calling Profile 2 windows "Profile 5") and blanked 4 more. See the note on
    // DetectFolder about why the layout path hasn't been switched over yet.

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint c);
        void GetAt(uint i, out PropertyKey k);
        void GetValue(ref PropertyKey k, out PropVariant v);
        void SetValue(ref PropertyKey k, ref PropVariant v);
        void Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore pps);

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

    private const int VT_LPWSTR = 31, VT_BSTR = 8;

    /// <summary>A window's AppUserModelID, or "" if it has none (most non-browser apps).</summary>
    public static string WindowAumid(IntPtr hwnd)
    {
        var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
        IPropertyStore? store = null;
        try
        {
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return "";
            var key = new PropertyKey { fmtid = new Guid("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"), pid = 5 };
            store.GetValue(ref key, out var pv);
            try
            {
                return pv.vt is VT_LPWSTR or VT_BSTR && pv.p != IntPtr.Zero
                    ? Marshal.PtrToStringUni(pv.p) ?? "" : "";
            }
            finally { PropVariantClear(ref pv); }
        }
        catch { return ""; }
        finally { if (store != null) Marshal.ReleaseComObject(store); }
    }

    /// <summary>The AppUserModelID's user-data segment for this browser: "User Data" -> "UserData".</summary>
    private static string UserDataToken(string proc)
    {
        var dir = UserDataDir(proc);
        var name = dir == null ? "" : Path.GetFileName(dir);
        return new string(name.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// The profile folder this window's AppUserModelID names ("Profile 5", "Default"), or null if
    /// the tag isn't one of this browser's normal profiles.
    /// </summary>
    private static string? ProfileFolderFromAumid(string aumid, string proc)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        var seg = aumid.Split('.');

        // Bare "Chrome": the Default profile of the normal user-data folder. Chrome drops the
        // suffix for it so those windows group under the plain pinned Chrome icon.
        if (seg.Length == 1) return "Default";

        // "Chrome.UserData.Profile5". The middle segment must be OUR user-data folder: an
        // automation/second instance tags windows "Chrome.mcpchrome316c96c.Default", which is a
        // different browser entirely and must not be folded in with the real Default profile.
        if (seg.Length == 3 && seg[1].Equals(UserDataToken(proc), StringComparison.OrdinalIgnoreCase))
        {
            string p = seg[2];
            if (p.Equals("Default", StringComparison.OrdinalIgnoreCase)) return "Default";
            var m = Regex.Match(p, @"^Profile\s*(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success) return $"Profile {m.Groups[1].Value}";
        }
        return null;
    }

    /// <summary>One browser window's profile: how to group it, and what to call it.</summary>
    /// <param name="Key">Groups windows of the same profile. The raw AppUserModelID, so two
    /// browser instances that both call a profile "Default" still can't collide.</param>
    /// <param name="Name">What to show a human ("Work"), or "" when we can't name it.</param>
    public readonly record struct WindowProfile(string Key, string Name);

    /// <summary>
    /// Which profile a browser window belongs to, for grouping open windows. Falls back to the
    /// window's own AppUserModelID as the group key when the profile isn't one we can name, so
    /// windows are still grouped the way the taskbar groups them.
    /// </summary>
    public static WindowProfile ProfileOf(IntPtr hwnd, string proc)
    {
        string aumid = WindowAumid(hwnd);
        if (string.IsNullOrEmpty(aumid)) return new WindowProfile("", "");
        string? folder = ProfileFolderFromAumid(aumid, proc);
        return new WindowProfile(aumid, folder == null ? "" : FriendlyName(proc, folder));
    }

    /// <summary>A profile folder's display name from the browser's own Local State
    /// ("Profile 5" -> "Work"), or the folder itself if it isn't listed.</summary>
    public static string FriendlyName(string proc, string folder)
    {
        var names = GetMap(proc).FolderNames;
        return names.TryGetValue(folder, out var n) && !string.IsNullOrWhiteSpace(n) ? n : folder;
    }
}
