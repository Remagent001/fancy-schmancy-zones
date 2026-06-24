# Fancy Schmancy Zones

A tiny Windows tray app that adds **lockable, named window layouts** on top of
[PowerToys FancyZones](https://learn.microsoft.com/windows/powertoys/fancyzones) —
arrange your windows once, **lock** that arrangement as a named layout, and **flip
between layouts** instantly.

It fills a long-standing FancyZones gap that lots of people have asked for
([PowerToys #16018](https://github.com/microsoft/PowerToys/issues/16018),
[#21192](https://github.com/microsoft/PowerToys/issues/21192)): treat a whole window
arrangement as one group you can toggle to.

> FancyZones still does the zone-snapping. This app just remembers each arrangement
> and brings it back — it doesn't replace or modify FancyZones.

## What it does

- **Lock a layout** — capture every window where it sits right now and save it under a name ("Coding", "Email", …).
- **Flip between layouts** — restore a saved layout: each window jumps back to its spot and comes to the front.
- **Manage** — rename, update to your current windows, or delete layouts from the tray menu.
- Runs quietly in the system tray. Your layouts are saved to `%APPDATA%\FancySchmancyZones\layouts.json`.

## Hotkeys

| Action | Keys |
| --- | --- |
| **Flip to next layout** (easiest) | **Double-tap `Ctrl`** |
| Flip to next layout | `Ctrl + Alt + Shift + Q` |
| Flip to previous layout | `Ctrl + Alt + Shift + W` |
| Lock current layout | `Ctrl + Alt + Shift + L` |

Hotkeys use a low-level keyboard listener so they keep working even when other
utilities have claimed common shortcuts. You can also do everything by
left- or right-clicking the tray icon.

## Install

1. Download **`FancySchmancyZones-Setup-x.y.z.exe`** from the
   [latest release](../../releases/latest).
2. Run it. It installs for the current user (no admin needed) and creates a Start Menu
   shortcut. You can optionally tick **Start automatically when I sign in**.
3. The app is self-contained — **no .NET install required.**

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and,
for the installer, [Inno Setup 6](https://jrsoftware.org/isdl.php).

```sh
# Run / build the app
cd src
dotnet build -c Release

# Produce the self-contained exe
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# Build the installer (writes dist/FancySchmancyZones-Setup-*.exe)
"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\FancySchmancyZones.iss
```

## Known limitation — Windows Virtual Desktops

Right now a layout only sees windows on the **current** virtual desktop. See
[the project notes](#) for why full multi-desktop support is hard (it needs
undocumented Windows APIs that change with every Windows update). It's on the roadmap.

## License

MIT — see [LICENSE](LICENSE).
