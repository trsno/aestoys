# aestoys

A small Windows tray app with desktop tweaks, built with .NET 10, WPF and [WPF-UI](https://github.com/lepoco/wpfui) (Fluent design).

## Features

### Alt+Tab per monitor
Alt+Tab lists only the windows on one monitor: either the monitor with the mouse cursor or the one with the active window. No Windhawk or DLL injection is involved. aestoys uses a low-level keyboard hook and draws its own switcher.

- **Grid**: a compact grid of icons and titles.
- **Coverflow** (optional): a full-screen 3D switcher with live window snapshots, inspired by the GNOME [CoverflowAltTab](https://github.com/dsheeler/CoverflowAltTab) extension. It has two styles:
  - **Coverflow**: the selected window sits flat in the centre and the others tilt away to the sides.
  - **Timeline**: the windows stack diagonally into the distance.

  It also has settings for vertical offset, title position, background dim and blur, window size ratio, easing and duration, icon style (Classic, Overlay or None), looping method, angles, spacing and scaling.

Keys while the switcher is open:

| Key | Action |
| --- | --- |
| Alt+Tab / Alt+Shift+Tab | Next / previous window |
| Arrow keys, mouse wheel | Move the selection |
| Esc | Cancel |
| Enter, release Alt, click | Switch to the selected window |
| Q / F4 | Close the selected window |
| D | Minimize every window on this monitor |
| Ctrl+Alt+Tab | The normal Windows switcher (all monitors) |

### Desktops and taskbar
Ported from the old `VirtualDesktopLoop.ahk` script. Each part can be turned off on the **Desktops and taskbar** page.

| Key / action | What it does |
| --- | --- |
| Ctrl+Win+Right on the last desktop | Goes to the first desktop (Ctrl+Win+Left on the first goes to the last). Elsewhere these keys work as normal in Windows. |
| Ctrl+Win+Shift+Left / Right | Moves the active window to the previous / next desktop and goes with it (wraps around). |
| Ctrl+Win+Shift+D | Shows the active window on every desktop, or back on one only. Pinned windows get a blue border. |
| Alt+Space | Centres the active window on its monitor. |

- **Desktop indicator**: the Windows logo on the Start button becomes a 2×2 desktop map. Each pane shows a desktop number and the current one stays lit. It hides while a fullscreen app runs.
- **Centred new windows**: app windows open in the middle of the monitor they appear on.
- **Start button launcher**: rest the mouse on Start and a bar of the shortcuts in a folder (default `C:\Warez\taskbar-launcher\shortcuts`) slides out next to it.

Jumping straight to a desktop, moving windows and pinning use [VirtualDesktopAccessor.dll](https://github.com/Ciantic/VirtualDesktopAccessor) (MIT), which ships next to `aestoys.exe` (see `ThirdParty/`). It relies on undocumented Windows internals, so aestoys checks it against the registry before every jump. Without the DLL, wrapping falls back to stepping through the desktops behind a still snapshot of the screen.

### General
- **Run on startup**: uses the HKCU Run key, or a scheduled task with highest privileges when administrator mode is on.
- **Run as administrator**: lets the switcher work while an elevated app has focus, and lets it preview elevated windows.

Settings are stored in `%AppData%\aestoys\settings.json`.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o C:\warez\aestoys
```

Running it requires the .NET 10 Desktop Runtime. Keep `VirtualDesktopAccessor.dll` next to `aestoys.exe`.

Test switches (they reach the copy that is already running): `--demo` and `--demo-all` show the switcher and write timings to `%AppData%\aestoys\perf.log`; `--demo-launcher` shows the launcher bar for a few seconds; `--page desktops` opens settings on that page.
