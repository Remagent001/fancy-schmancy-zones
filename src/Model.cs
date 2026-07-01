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

/// <summary>Everything we persist between runs.</summary>
public sealed class AppState
{
    public List<LockedLayout> Layouts { get; set; } = new();

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
