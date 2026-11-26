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

### General
- **Run on startup**: uses the HKCU Run key, or a scheduled task with highest privileges when administrator mode is on.
- **Run as administrator**: lets the switcher work while an elevated app has focus, and lets it preview elevated windows.

Settings are stored in `%AppData%\aestoys\settings.json`.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o C:\warez\aestoys
```

Running it requires the .NET 10 Desktop Runtime.
