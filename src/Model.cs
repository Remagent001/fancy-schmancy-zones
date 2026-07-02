using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FancySchmancyZones;

/// <summary>A window position/size in screen pixels.</summary>
public readonly record struct Rect(int X, int Y, int W, int H);

/// <summary>One remembered window inside a locked layout.</summary>
public sealed class SavedWindow
{
    public string Title { get; set; } = "";
    public string Process { get; set; } = "";

    // Full path to the program's .exe, captured at lock time so we can relaunch it later.
    public string ExePath { get; set; } = "";

    // For Chrome/Edge: the profile folder this window used (e.g. "Profile 10"), so we relaunch
    // the right profile and drop it in the right spot. Empty for non-browser windows.
    public string Profile { get; set; } = "";

    public Rect Bounds { get; set; }

    // Live handle for this session only — never persisted (handles are not stable across restarts).
    [JsonIgnore] public IntPtr Hwnd { get; set; }
}

/// <summary>A named, locked layout: a set of windows and where they belong.</summary>
public sealed class LockedLayout
{
    public string Name { get; set; } = "";
    public List<SavedWindow> Windows { get; set; } = new();
}

/// <summary>User-adjustable app behavior, persisted alongside layouts.</summary>
public sealed class AppSettings
{
    // When on, layouts remember which Chrome/Edge profile each browser window used, and
    // reopen/match that same profile. Turn off to treat all browser windows generically
    // (the pre-0.5.0 behavior) — e.g. if profile detection ever picks the wrong window.
    public bool MatchBrowserProfiles { get; set; } = true;

    // When on, switching to a layout minimizes every open window that ISN'T part of that
    // layout — leftovers from another layout, or anything opened since — so the layout you
    // switch to is never left partly hidden behind something else.
    public bool MinimizeOtherWindows { get; set; } = true;
}

/// <summary>Everything we persist between runs.</summary>
public sealed class AppState
{
    public List<LockedLayout> Layouts { get; set; } = new();
    public AppSettings Settings { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FancySchmancyZones");

    public static string FilePath => Path.Combine(Dir, "layouts.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), JsonOpts) ?? new AppState();
        }
        catch { /* corrupt or unreadable — start clean rather than crash */ }
        return new AppState();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
