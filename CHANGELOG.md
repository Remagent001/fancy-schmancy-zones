# Changelog

## 0.7.0 — 2026-07-01

- **Switching layouts now minimizes everything else.** By default, when you flip to a layout,
  any window that isn't part of it — leftovers from another layout, or anything you opened since
  — gets minimized. No more digging through unrelated windows on top of the one you switched to.
- New setting: **Settings → "Minimize other windows when switching layouts"** — turn off to go
  back to the old behavior (only the layout's own windows are moved/raised; everything else is
  left alone).

## 0.6.0 — 2026-07-01

- **Edge profiles now work too** (previously only Chrome). Edge labels its profile button
  differently under the hood; detection now understands both.
- **Fixed a real bug:** if two browser profiles shared the exact same display name, the app
  could previously guess wrong about which one a window belonged to. Now it recognizes the
  name is ambiguous and safely falls back to normal (profile-blind) matching for those windows,
  instead of guessing.
- When you lock a layout and some profiles couldn't be told apart, you'll get a heads-up
  explaining why (and that renaming the profile in the browser fixes it) — nothing is silent.
- **New setting:** tray → **Settings → "Match Chrome/Edge browser profiles"** — turn this off to
  go back to treating all browser windows generically (the pre-0.5.0 behavior), no renaming
  required.
- "How it works" now explains both the app-launching and browser-profile features.

## 0.5.0 — 2026-06-30

- **Chrome/Edge profiles are now remembered.** A layout records *which profile* each browser
  window used, so "Open apps + arrange" reopens the correct profile in the correct spot — not
  just a default Chrome window. Works across your 8 profiles.
- How it reads the profile: Chromium doesn't expose it through the window or process, so the app
  reads the toolbar's profile button (the little avatar) via Windows accessibility and maps that
  name to Chrome's profile folder. Detection is ~10ms per window.
- Tip: if two profiles share the exact same display name, give one a distinct name in Chrome so
  the app can tell them apart.

## 0.4.0 — 2026-06-30

- **New: launch a layout's apps automatically.** Layouts now also remember each window's
  program file (its `.exe`), so a layout can rebuild itself from scratch — handy after a reboot
  when none of the apps are open yet.
- In the tray menu, under **Manage layouts → [layout name]**, there's a new
  **"Open apps + arrange"** option. It opens any of that layout's apps that aren't already
  running, waits for their windows to appear, then snaps everything into the saved positions.
  Apps that are already open aren't reopened.
- Note: this relaunches the *program*, not the exact document/tab you had — e.g. it reopens your
  browser, but not the specific pages. Flipping (double-tap Ctrl) is unchanged and still only
  arranges windows that are already open.

## 0.3.3 — 2026-06-25

- **Really fixed the hang this time.** The freeze came from an old "force focus" trick
  (`AttachThreadInput`) that ties our app's input to the app we're switching to — and if that app
  (e.g. Outlook) is busy, both lock up. Removed it. All window moves are now non-blocking
  (`ShowWindowAsync` + `SWP_ASYNCWINDOWPOS`) and run on a background thread, so a flip can never
  freeze the app or your keyboard.

## 0.3.2 — 2026-06-25

- **Fixed: app could hang/crash** (Windows "app hang") when saving a layout or flipping near a
  busy app like Outlook. The global keyboard listener now runs on its own dedicated thread, so it
  can never be blocked by window work — which was stalling keyboard input system-wide.
- Added a crash/error safety net: unexpected errors are logged to
  `%APPDATA%\FancySchmancyZones\crash.log` instead of closing the app.
- Note: the `Ctrl+Alt+Shift+Q/W/L` combos may be intercepted by other keyboard utilities on some
  PCs. **Double-tap Ctrl** (flip) is the reliable gesture; lock/manage are always on the tray menu.

## 0.3.1 — 2026-06-24

- **Added: double-tap `Ctrl` to flip to the next layout** — an easy gesture that's hard for other
  utilities to block.
- **Fixed: hotkeys did nothing.** The internal helper that runs an action never had its window
  handle created, so every keypress silently failed. Hotkeys (and double-tap) now work.
- Tray menu and help updated to show the double-tap.

## 0.3.0 — 2026-06-23

First public release.

- Lock the current window arrangement as a named layout; flip between layouts.
- Hotkeys via a low-level keyboard listener (works even when other utilities have
  claimed common shortcuts): `Ctrl+Alt+Shift+L` lock, `Ctrl+Alt+Shift+Q` next,
  `Ctrl+Alt+Shift+W` previous.
- Left- or right-click the tray icon to pick, rename, update, or delete layouts.
- One-click, per-user Windows installer (no admin, no .NET prerequisite) with an
  optional "start at sign-in" choice.
- App icon.

### Known limitation
- Layouts only see windows on the current Windows Virtual Desktop. Full multi-desktop
  support is on the roadmap (needs undocumented, version-specific Windows APIs).
